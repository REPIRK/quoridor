using System.Diagnostics;
using Quoridor.Core;

namespace Quoridor.Engine;

public readonly record struct SearchResult(Move Move, int Score, int Depth, long Nodes)
{
    public bool IsForced => Math.Abs(Score) >= Evaluation.MateThreshold;
}

/// <summary>
/// One search thread: alpha-beta with a principal variation search, iterative
/// deepening, aspiration windows, killers, history, late move reductions, and an
/// exact verdict for wall-less races.
///
/// The transposition table is passed in rather than owned, because under lazy SMP
/// every thread runs its own <see cref="SearchEngine"/> over the same table — the
/// table is how the threads help each other.
/// </summary>
public sealed class SearchEngine
{
    public const int MaxPly = 64;

    private const int Infinity = Evaluation.Mate * 2;
    private const int TimeCheckInterval = 1023;

    private readonly TranspositionTable _table;
    private readonly EvaluationWeights _weights;
    private readonly Move[] _moveStack = new Move[MaxPly * MoveCandidates.MaxMoves];
    private readonly Move?[,] _killers = new Move?[MaxPly, 2];
    private readonly int[,] _history = new int[3, Board.CellCount];

    private ulong[] _hashPath = new ulong[MaxPly + 512];
    private int _rootHistory;

    private Stopwatch _clock = new();
    private TimeSpan _budget;
    private CancellationToken _token;
    private bool _stopped;

    private readonly EngineOptions _options;
    private readonly int _threadIndex;

    public SearchEngine(
        TranspositionTable table,
        EvaluationWeights? weights = null,
        EngineOptions? options = null,
        int threadIndex = 0)
    {
        _table = table;
        _weights = weights ?? EvaluationWeights.Default;
        _options = options ?? EngineOptions.Default;
        _threadIndex = threadIndex;
    }

    public long Nodes { get; private set; }

    public int CompletedDepth { get; private set; }

    /// <summary>
    /// Searches until the budget runs out or <paramref name="maxDepth"/> is reached.
    /// <paramref name="startDepth"/> lets helper threads begin deeper than the main
    /// one, which is what gives lazy SMP its variety.
    /// </summary>
    public SearchResult Search(
        in GameState root,
        int maxDepth,
        Stopwatch clock,
        TimeSpan budget,
        ReadOnlySpan<ulong> positionHistory,
        CancellationToken token,
        int startDepth = 1)
    {
        _clock = clock;
        _budget = budget;
        _token = token;
        _stopped = false;
        Nodes = 0;
        CompletedDepth = 0;

        Array.Clear(_killers);
        Array.Clear(_history);

        LoadHistory(positionHistory);

        Span<Move> rootMoves = MoveSlice(0);
        int rootCount = MoveCandidates.Generate(root, rootMoves, MoveCandidates.MaxWalls, scoreWalls: true);

        // Nothing to choose between, so nothing is searched — and the result says exactly
        // that rather than claiming a one-node search that came out level. Depth 0 and
        // zero nodes is what happened, it agrees with CompletedDepth, and both front ends
        // already read Depth 0 as "there is no engine line to show", which is the truth
        // here. The score is the position's static worth, so a caller that does read it
        // gets a forced loss reported as a forced loss: the position that named this
        // reported Score 0 while Evaluation.RaceScore on the same board said -999,999.
        //
        // Zero candidates is not the same claim as "no legal move": MoveCandidates keeps
        // only the walls near a route or beside an existing one, so a boxed-in pawn with
        // every nearby slot taken comes back empty while legal walls remain elsewhere.
        // HeuristicAgent.Fallback asks the rules rather than the generator.
        if (rootCount <= 1)
        {
            Move only = rootCount == 1 ? rootMoves[0] : HeuristicAgent.Fallback(root);
            return new SearchResult(only, StaticScore(root), 0, 0);
        }

        Move best = rootMoves[0];
        int score = 0;

        for (int depth = Math.Max(1, startDepth); depth <= maxDepth; depth++)
        {
            int iterationScore = SearchRootWithAspiration(root, depth, score, ref best);

            if (_stopped) break;

            score = iterationScore;
            CompletedDepth = depth;

            // A forced result will not change with more depth.
            if (Math.Abs(score) >= Evaluation.MateThreshold) break;

            // No point starting a depth we clearly cannot finish.
            if (_clock.Elapsed > TimeSpan.FromTicks(_budget.Ticks / 2)) break;
        }

        return new SearchResult(best, score, CompletedDepth, Nodes);
    }

    /// <summary>
    /// What the position is worth with no search at all: the ladder <see cref="Negamax"/>
    /// reaches at a leaf, minus the two terms that only mean anything part-way down a
    /// search — a repetition against the path above it, and the ply cap.
    ///
    /// Deliberately not shared with Negamax's copy. Negamax tests repetition between the
    /// finished-game test and the race verdict, so folding the two into one method would
    /// move the verdict ahead of the repetition test and change what a repeated wall-less
    /// position scores. That is a search change wearing a refactor.
    /// </summary>
    private int StaticScore(in GameState state)
    {
        if (state.IsGameOver)
            return state.Winner == state.SideToMove ? Evaluation.Mate : -Evaluation.Mate;

        if (state.WallsOf(0) == 0 && state.WallsOf(1) == 0 && !state.HasPickups && !state.HasPortals)
        {
            int race = Evaluation.RaceScore(state, 0);
            if (race != Evaluation.Unknown) return race;
        }

        return Evaluation.Evaluate(state, state.SideToMove, _weights);
    }

    // =================================================================== root ==

    private int SearchRootWithAspiration(in GameState root, int depth, int previousScore, ref Move best)
    {
        // Shallow searches are cheap and their scores are unreliable, so only narrow
        // the window once there is a previous score worth trusting.
        if (!_options.UseAspirationWindows || depth < 4 || Math.Abs(previousScore) >= Evaluation.MateThreshold)
            return SearchRoot(root, depth, -Infinity, Infinity, ref best);

        // One step of route is worth 100 and the race verdict swings by hundreds, so a
        // tight window here would fail on almost every iteration.
        int window = 140;

        while (true)
        {
            int alpha = previousScore - window;
            int beta = previousScore + window;

            int score = SearchRoot(root, depth, alpha, beta, ref best);
            if (_stopped) return score;

            if (score > alpha && score < beta) return score;

            // Fell outside the window: widen and try again rather than trusting a
            // bound as if it were a score.
            window *= 4;
            if (window > 4000) return SearchRoot(root, depth, -Infinity, Infinity, ref best);
        }
    }

    private int SearchRoot(in GameState root, int depth, int alpha, int beta, ref Move best)
    {
        Span<Move> moves = MoveSlice(0);
        int count = MoveCandidates.Generate(root, moves, MoveCandidates.MaxWalls, scoreWalls: true);

        EnsureFirst(moves, ref count, best, 0);

        // Helper threads keep the principal move first but take a different second
        // choice, so they explore a different shape of tree and the shared table ends
        // up with more than one thread's worth of knowledge in it. Without this they
        // simply repeat the main thread and measure as no gain at all.
        if (_threadIndex > 0 && count > 2)
        {
            int alternative = 1 + (_threadIndex - 1) % (count - 1);
            (moves[1], moves[alternative]) = (moves[alternative], moves[1]);
        }

        _hashPath[_rootHistory] = root.Hash;

        int bestScore = -Infinity;

        for (int i = 0; i < count; i++)
        {
            GameState next = root;
            next.Apply(moves[i]);

            int score;
            if (i == 0)
            {
                score = Child(root, next, depth - 1, 1, alpha, beta);
            }
            else
            {
                score = Child(root, next, depth - 1, 1, alpha, alpha + 1);
                if (score > alpha && score < beta)
                    score = Child(root, next, depth - 1, 1, alpha, beta);
            }

            if (_stopped) break;

            if (score > bestScore) bestScore = score;

            // Publish only on a move that actually raises alpha. Every other score here
            // came from a null window and is nothing but an upper bound — picking the
            // largest of those would be picking the largest of several "at most" claims.
            // This also means a half-finished deeper iteration still improves the answer,
            // and a window that fails low leaves the previous iteration's move standing.
            if (score > alpha)
            {
                alpha = score;
                best = moves[i];
            }

            if (alpha >= beta) break;
        }

        return bestScore;
    }

    // ================================================================ negamax ==

    /// <summary>
    /// Searches a position one move on. Normally the turn has passed, so the child's
    /// score is the opponent's and gets negated with the window flipped — the usual
    /// negamax step. A free move picked up off the board does not pass the turn, and
    /// then the child is scored from the same side and must be taken as it is.
    /// </summary>
    private int Child(in GameState parent, in GameState next, int depth, int ply, int alpha, int beta)
    {
        return next.SideToMove == parent.SideToMove
            ? Negamax(next, depth, ply, alpha, beta)
            : -Negamax(next, depth, ply, -beta, -alpha);
    }

    private int Negamax(in GameState state, int depth, int ply, int alpha, int beta)
    {
        if (_stopped) return 0;

        Nodes++;
        if ((Nodes & TimeCheckInterval) == 0) CheckTime();

        // Reaching the goal row is what handed the turn over, so a finished game is a
        // loss for the side to move. The ply term prefers faster wins. Asking who won
        // rather than assuming costs one comparison in a branch that is already rare,
        // and means a free move that ever did leave its winner on move would score as a
        // win here instead of silently inverting the whole subtree.
        if (state.IsGameOver)
        {
            return state.Winner == state.SideToMove
                ? Evaluation.Mate - ply
                : -(Evaluation.Mate - ply);
        }

        // Quoridor has no draw rule, so a repetition is not a result — it is a wasted
        // pair of moves. Scoring it slightly against the side to move stops the engine
        // treating a shuffle as a comfortable place to sit.
        if (IsRepetition(state.Hash, ply)) return -25;
        if (ply >= MaxPly - 2) return Evaluation.Evaluate(state, state.SideToMove, _weights);

        // With no walls left the game is a settled race; no amount of search changes it.
        // Unless there are pickups still lying about, which can hand out both a wall and
        // an extra move — and then the race is not settled at all.
        //
        // Portals are excluded for a different reason. The margin the race verdict allows
        // itself was measured against pawns obstructing each other, whose worst case is a
        // corridor where the blocked player cannot get past — and a portal is a corridor
        // of degree one: stand on one mouth with the opponent on the other and all four
        // of its sides walled, and there is no move through it at all, while the fill
        // treats pawns as transparent and routes straight through. That is the measured
        // case in its most extreme form, so the measured margin is not entitled to cover
        // it. A verdict here outranks the mate threshold and ends iterative deepening, so
        // a wrong one is played out to the end and never looked at again; until the exact
        // solver has been re-run over portal boards, portals simply do not get a verdict.
        if (state.WallsOf(0) == 0 && state.WallsOf(1) == 0 && !state.HasPickups && !state.HasPortals)
        {
            int race = Evaluation.RaceScore(state, ply);
            if (race != Evaluation.Unknown) return race;
        }

        if (depth <= 0) return Evaluation.Evaluate(state, state.SideToMove, _weights);

        bool isPrincipalVariation = beta - alpha > 1;
        int alphaOriginal = alpha;

        Move tableMove = default;
        bool hasTableMove = false;

        if (_options.UseTranspositionTable && _table.TryGet(state.Hash, out TableEntry entry))
        {
            // A shared table plus hash collisions means a stored move can be nonsense
            // in this position; never trust one without checking it.
            if (entry.HasMove && MoveCandidates.IsLegal(state,entry.Move))
            {
                tableMove = entry.Move;
                hasTableMove = true;
            }

            if (!isPrincipalVariation && entry.Depth >= depth)
            {
                int stored = FromTable(entry.Score, ply);

                switch (entry.Bound)
                {
                    case Bound.Exact: return stored;
                    case Bound.Lower when stored >= beta: return stored;
                    case Bound.Upper when stored <= alpha: return stored;
                }
            }
        }

        bool wallsScored = _options.ScoreWallsEverywhere || ScoreWalls(depth);

        Span<Move> moves = MoveSlice(ply);
        int count = MoveCandidates.Generate(state, moves, WallLimit(depth), wallsScored);

        int ordered = 0;
        if (hasTableMove) EnsureFirst(moves, ref count, tableMove, ordered++);

        for (int slot = 0; slot < 2; slot++)
        {
            if (_killers[ply, slot] is not { } killer) continue;
            if (!MoveCandidates.IsLegal(state, killer)) continue;
            if (Hoist(moves, count, killer, ordered)) ordered++;
        }

        // Where the generator could not afford to measure what each wall does, history
        // is the only signal left — and it is a good one, since a wall that keeps
        // causing cutoffs elsewhere in the tree usually does so here too. Where the
        // generator did measure, its own order is better and is left alone.
        if (_options.UseHistoryOrdering && !wallsScored)
            SortTailByHistory(moves, ordered, count);

        if (count == 0) return Evaluation.Evaluate(state, state.SideToMove, _weights);

        _hashPath[_rootHistory + ply] = state.Hash;

        int bestScore = -Infinity;
        Move bestMove = moves[0];

        for (int i = 0; i < count; i++)
        {
            Move move = moves[i];

            GameState next = state;
            next.Apply(move);

            int score;

            if (i == 0)
            {
                score = Child(state, next, depth - 1, ply + 1, alpha, beta);
            }
            else
            {
                // Late move reductions: the ordering is good enough that walls tried
                // this far down rarely deserve full depth. Anything that beats alpha
                // is re-searched properly, so a wrong guess costs time, not accuracy.
                int reduction = 0;
                if (_options.UseLateMoveReductions && depth >= 3 && i >= 3 && !isPrincipalVariation && move.IsWall)
                {
                    reduction = i >= 10 ? 2 : 1;

                    // A move that has been causing cutoffs all over the tree has earned
                    // the benefit of the doubt.
                    if (_history[(int)move.Kind, HistoryIndex(move)] > 2000) reduction--;
                }

                score = Child(state, next, depth - 1 - reduction, ply + 1, alpha, alpha + 1);

                if (score > alpha && reduction > 0)
                    score = Child(state, next, depth - 1, ply + 1, alpha, alpha + 1);

                if (score > alpha && score < beta)
                    score = Child(state, next, depth - 1, ply + 1, alpha, beta);
            }

            if (_stopped) return 0;

            if (score > bestScore)
            {
                bestScore = score;
                bestMove = move;
            }

            if (score > alpha) alpha = score;

            if (alpha >= beta)
            {
                RememberCutoff(move, depth, ply);
                break;
            }
        }

        Bound bound = bestScore <= alphaOriginal ? Bound.Upper
            : bestScore >= beta ? Bound.Lower
            : Bound.Exact;

        _table.Store(state.Hash, ToTable(bestScore, ply), depth, bound, bestMove, hasMove: true);

        return bestScore;
    }

    // ============================================================== ordering ==

    private void RememberCutoff(Move move, int depth, int ply)
    {
        if (_killers[ply, 0] != move)
        {
            _killers[ply, 1] = _killers[ply, 0];
            _killers[ply, 0] = move;
        }

        _history[(int)move.Kind, HistoryIndex(move)] += depth * depth;
    }

    /// <summary>
    /// Reorders the moves that were not hoisted, best history first. Pawn steps keep
    /// their place ahead of every wall: they are almost always the move, and the
    /// generator already sorted them by the progress they make.
    /// </summary>
    private void SortTailByHistory(Span<Move> moves, int from, int count)
    {
        const int pawnPriority = 1 << 24;

        Span<int> keys = stackalloc int[MoveCandidates.MaxMoves];

        for (int i = from; i < count; i++)
        {
            Move move = moves[i];
            keys[i] = (move.IsWall ? 0 : pawnPriority) + _history[(int)move.Kind, HistoryIndex(move)];
        }

        for (int i = from + 1; i < count; i++)
        {
            Move move = moves[i];
            int key = keys[i];

            int j = i - 1;
            while (j >= from && keys[j] < key)
            {
                moves[j + 1] = moves[j];
                keys[j + 1] = keys[j];
                j--;
            }

            moves[j + 1] = move;
            keys[j + 1] = key;
        }
    }

    private static int HistoryIndex(Move move) =>
        move.Kind == MoveKind.Pawn ? move.Cell : Board.SlotIndex(move.Row, move.Col);

    /// <summary>Moves <paramref name="move"/> to <paramref name="index"/> if it is present.</summary>
    private static bool Hoist(Span<Move> moves, int count, Move move, int index)
    {
        for (int i = index; i < count; i++)
        {
            if (moves[i] != move) continue;

            (moves[index], moves[i]) = (moves[i], moves[index]);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Like <see cref="Hoist"/>, but appends the move when the candidate generator
    /// pruned it away. Table moves have earned the right to be searched.
    /// </summary>
    private static void EnsureFirst(Span<Move> moves, ref int count, Move move, int index)
    {
        if (Hoist(moves, count, move, index)) return;

        if (count >= moves.Length) return;

        moves[count] = move;
        count++;
        Hoist(moves, count, move, index);
    }

    private Span<Move> MoveSlice(int ply) =>
        _moveStack.AsSpan(ply * MoveCandidates.MaxMoves, MoveCandidates.MaxMoves);

    private static int WallLimit(int depth) => depth switch
    {
        >= 6 => 22,
        5 => 18,
        4 => 14,
        3 => 12,
        2 => 8,
        _ => 6,
    };

    /// <summary>
    /// Ranking walls by their true effect on both routes costs four flood fills each.
    /// That is worth paying near the root and far too slow at the leaves, where a
    /// proximity heuristic orders nearly as well.
    /// </summary>
    private static bool ScoreWalls(int depth) => depth >= 3;

    // =============================================================== bookkeeping ==

    private void LoadHistory(ReadOnlySpan<ulong> positionHistory)
    {
        int needed = positionHistory.Length + MaxPly + 8;
        if (_hashPath.Length < needed) _hashPath = new ulong[needed];

        positionHistory.CopyTo(_hashPath);
        _rootHistory = positionHistory.Length;
    }

    /// <summary>
    /// True when this position already occurred on the way here. Only every second
    /// ancestor can match, since the side to move has to be the same.
    /// </summary>
    private bool IsRepetition(ulong hash, int ply)
    {
        for (int i = _rootHistory + ply - 2; i >= 0; i -= 2)
            if (_hashPath[i] == hash) return true;

        return false;
    }

    private void CheckTime()
    {
        if (_token.IsCancellationRequested || _clock.Elapsed >= _budget) _stopped = true;
    }

    private static int ToTable(int score, int ply) =>
        score >= Evaluation.MateThreshold ? score + ply
        : score <= -Evaluation.MateThreshold ? score - ply
        : score;

    private static int FromTable(int score, int ply) =>
        score >= Evaluation.MateThreshold ? score - ply
        : score <= -Evaluation.MateThreshold ? score + ply
        : score;
}
