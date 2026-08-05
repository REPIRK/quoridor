using System.Diagnostics;
using Quoridor.Core;

namespace Quoridor.Engine;

/// <summary>
/// The playing strength of the project: an alpha-beta engine behind the
/// <see cref="IQuoridorAgent"/> seam.
///
/// With more than one thread this runs lazy SMP — every thread searches the same
/// root over the shared transposition table, helpers starting a ply or two deeper so
/// they explore a different shape of tree. Nobody merges results; the speed-up comes
/// from the table being filled faster than one thread could fill it. The main
/// thread's answer is the one that is played.
/// </summary>
public sealed class SearchAgent : IQuoridorAgent
{
    /// <summary>
    /// The longest one ponder is allowed to run, however long the opponent takes over the
    /// rest of their turn. The deepening loop stops starting iterations it cannot finish
    /// in half the budget, so in practice a ponder ends between seven and fifteen seconds
    /// — which is the intent rather than an accident. This engine writes on the order of
    /// a million entries a second, so somewhere around here a ponder fills the table and
    /// begins competing with itself for slots, evicting its own earlier work and the
    /// previous real search's along with it. A human who thinks for a minute is not made
    /// any better off by the last forty-five seconds of it.
    /// </summary>
    private static readonly TimeSpan PonderBudget = TimeSpan.FromSeconds(15);

    private readonly TranspositionTable _table;
    private readonly SearchEngine[] _engines;
    private readonly int _maxDepth;

    /// <summary>
    /// The engine that searches while the opponent is on move. It is deliberately not
    /// one of <see cref="_engines"/>: a <see cref="SearchEngine"/> keeps the whole of
    /// its working state in instance fields — move stack, killers, history, and the flag
    /// that says to stop — so the only way a ponder can be unable to corrupt a real
    /// search is for the two never to be the same object. Null unless pondering was
    /// asked for, so an agent that will never ponder allocates exactly what it did before.
    /// </summary>
    private readonly SearchEngine? _ponderEngine;

    /// <summary>0 while the ponder engine is free, 1 while it is searching.</summary>
    private int _pondering;

    private ulong[] _positionHistory = Array.Empty<ulong>();

    public SearchAgent(
        int maxDepth = 32,
        TimeSpan? moveTime = null,
        int threads = 1,
        int tableMegabytes = 64,
        EvaluationWeights? weights = null,
        EngineOptions? options = null,
        bool ponder = false)
    {
        _maxDepth = Math.Clamp(maxDepth, 1, SearchEngine.MaxPly - 4);
        DefaultMoveTime = moveTime ?? TimeSpan.FromMilliseconds(1000);
        MoveTime = DefaultMoveTime;
        _table = new TranspositionTable(tableMegabytes);

        int count = Math.Clamp(threads, 1, Math.Max(1, Environment.ProcessorCount));
        _engines = new SearchEngine[count];
        for (int i = 0; i < count; i++) _engines[i] = new SearchEngine(_table, weights, options, threadIndex: i);

        // Thread index zero: a non-zero one makes the root swap its second move for a
        // different one, which is how lazy SMP helpers avoid repeating the main thread.
        // Nobody else is searching this root, so there is nothing to differ from and an
        // honest move order is worth more than a varied one.
        if (ponder) _ponderEngine = new SearchEngine(_table, weights, options, threadIndex: 0);
    }

    public string Name => "Bot · Hard";

    /// <summary>The budget this agent was built with, and its ceiling.</summary>
    public TimeSpan DefaultMoveTime { get; }

    /// <summary>
    /// Budget for the next search. A caller running a chess clock lowers this so the
    /// engine cannot think its way into losing on time.
    /// </summary>
    public TimeSpan MoveTime { get; set; }

    /// <summary>Details of the most recent search. Useful for benchmarking and tuning.</summary>
    public SearchResult LastResult { get; private set; }

    public int Threads => _engines.Length;

    /// <summary>Whether this agent was built with an engine to spare for pondering.</summary>
    public bool CanPonder => _ponderEngine is not null;

    public void SetGameHistory(ReadOnlySpan<ulong> positionHashes)
    {
        if (_positionHistory.Length != positionHashes.Length)
            _positionHistory = new ulong[positionHashes.Length];

        positionHashes.CopyTo(_positionHistory);
    }

    public Move ChooseMove(in GameState state, CancellationToken cancellationToken = default)
    {
        GameState root = state;
        ulong[] history = _positionHistory;

        _table.NewGeneration();
        var clock = Stopwatch.StartNew();

        SearchResult result = _engines.Length == 1
            ? _engines[0].Search(root, _maxDepth, clock, MoveTime, history, cancellationToken)
            : SearchInParallel(root, history, clock, cancellationToken);

        LastResult = result;

        // A move can only reach here after being generated and validated, but the
        // table is shared and lock-free, so make the guarantee explicit.
        return MoveCandidates.IsLegal(root, result.Move) ? result.Move : HeuristicAgent.Fallback(root);
    }

    /// <summary>
    /// Searches for as long as the token allows, purely to fill the shared table for the
    /// search that will follow. <paramref name="state"/> is the opponent's own position,
    /// the one they are deciding what to do with — which is the position the expected
    /// reply is read out of and applied to, not the position searched.
    ///
    /// It searches the position after the reply it expects, and falls back to
    /// <paramref name="state"/> itself when it has no expectation. That was chosen by
    /// measurement and not by argument. Both were built and run as three arms off the
    /// same positions on the same budgets, each paired against the same unpondered
    /// search, by <c>Quoridor.Bench ponderhit</c>:
    ///
    ///                              heuristic opponent      engine opponent
    ///   ponder nothing                    0.00 ply              0.00 ply
    ///   ponder the parent                +0.54                 +0.71
    ///   ponder the prediction            +0.99                 +1.56
    ///   ponder the right position        +1.81                 +1.73     (unreachable)
    ///                                    n=105                 n=209
    ///
    /// Differenced reply by reply, which cancels the position and leaves an error bar
    /// worth reading, the prediction is worth +0.45 +/- 0.09 ply more than the parent
    /// against the heuristic and +0.85 +/- 0.10 against an engine. The heuristic is the
    /// number to believe: it plays less predictably, which is what a person does, and the
    /// engine expected its reply 48% of the time against 83% for another engine.
    ///
    /// So a miss is common and it does cost something — on the plies where the guess was
    /// wrong the prediction arm came out 0.18 +/- 0.06 ply behind the parent arm against
    /// the heuristic. That is the price, it is already inside the pooled figures above,
    /// and the hits pay it several times over.
    ///
    /// It returns nothing on purpose. There is no result here that could reach the board
    /// because there is no result at all — nothing is written to <see cref="LastResult"/>,
    /// nothing to <see cref="MoveTime"/>, nothing to the stored game history, and no new
    /// table generation is opened. All of those belong to the move actually being played.
    /// Predicting a reply is a choice of what to search, not a claim about what to play,
    /// and the prediction itself is never returned.
    ///
    /// The history is a parameter rather than a call to <see cref="SetGameHistory"/>
    /// because that method replaces the array <see cref="ChooseMove"/> reads, and a
    /// ponder has no business touching what the next real search will be given.
    ///
    /// When a prediction misses, the table is left holding analysis of a game that did
    /// not happen — and almost all of it is useless rather than wrong. An entry is
    /// indexed by position, and what a position is worth does not depend on the route
    /// that reached it; that is the whole reason a transposition table is allowed to
    /// exist at all. The next real search reads those entries two ways, and neither is a
    /// way to lose. A stored move is an ordering hint, put through
    /// <see cref="MoveCandidates.IsLegal"/> at every node before it is tried, so the
    /// worst a stale one costs is the time to search a move that did not deserve to go
    /// first. A stored score is taken as a bound only when it was searched at least as
    /// deep and only outside the principal variation, and it is a correct score for that
    /// position whichever game arrived at it — with the one exception set out where the
    /// history is built below, which is the only thing here that can be wrong rather
    /// than merely wasted.
    ///
    /// That was checked rather than argued, by <c>Quoridor.Bench pondermiss</c>, which
    /// takes real misses out of real games and runs the real search after them at fixed
    /// depth so the answers repeat. Over 173 of them: no arm ever produced an illegal
    /// move; the real search moved off its cold answer 8 times after a wrong ponder and
    /// 10 times after the parent ponder that used to ship; and where the answers differed
    /// they were adjudicated by a deeper search, which put the wrong ponder ahead of cold
    /// on average and not behind the parent ponder. A warmed table changes answers on
    /// purpose — an entry searched deeper than the running iteration is grafted in — so
    /// "the same move as cold" was never the bar and no ponder of any kind clears it.
    /// The bar is that a wrong guess is no more dangerous than what already shipped, and
    /// it is not.
    /// </summary>
    public void Ponder(in GameState state, ReadOnlySpan<ulong> positionHistory, CancellationToken token)
    {
        if (_ponderEngine is null || token.IsCancellationRequested) return;

        // Two searches on one engine is the bug this whole arrangement exists to make
        // impossible, so a second ponder is refused here rather than left to every
        // caller to sequence for itself.
        if (Interlocked.Exchange(ref _pondering, 1) != 0) return;

        try
        {
            GameState root = state;
            ReadOnlySpan<ulong> history = positionHistory;

            // Held alive for as long as the span into it is used. A span cannot be taken
            // of a local that only exists inside the branch that filled it.
            ulong[]? extended = null;

            // The repetition history, which is the thing this design has to get right.
            //
            // SearchEngine.IsRepetition walks _hashPath: the history it was handed, then
            // the root, then the live search path. Pondering the parent had exact indices
            // for free — every position in that array is one the game really passed
            // through, and the root is the one it is standing in. Pondering a prediction
            // does not get that for free, and the difference is exactly one index.
            //
            // Everything below the root is still exact, because the history built here is
            // the real game plus the parent and the parent is where the game is sitting
            // while the opponent thinks. Nothing about it is a guess. The root is the
            // speculative index: a line inside the ponder that shuffles back onto the
            // predicted position scores as a repetition, and if the opponent plays
            // something else that was a claim about a game that never happened.
            //
            // That is bounded here rather than solved, and the bound is three things.
            // First, no entry ever carries the claim: IsRepetition returns its -25 before
            // Negamax reaches its Store, so the repeating node itself is never written,
            // and only ancestors inside the ponder subtree can back a spurious one up —
            // down lines that returned to the predicted position without placing a wall,
            // since walls are never taken off the board again. Second, such an ancestor
            // can be read by the next real search and hand it a bound that is wrong
            // rather than merely useless, which is the one way this can be wrong at all;
            // it is also the unsoundness every search already lives with, since interior
            // nodes are stored all the time with scores that depended on repetitions
            // against the searching line and are read back by later searches that never
            // stood on it. This adds one more speculative ancestor to a path that already
            // carried a ply's worth. Third, none of it can produce a wrong move: every
            // move that comes back out of the table is put through
            // MoveCandidates.IsLegal before it is searched, and ChooseMove checks its own
            // answer once more before returning it.
            //
            // That is a bound and not a proof, so it was also measured: pondermiss found
            // no illegal move and no systematic loss of quality over 173 real misses, and
            // found the parent ponder that used to ship perturbing the real search's
            // answer slightly more often than a wrong guess does. The reason is the same
            // one that makes a miss cheap in general — a ponder on a position the game
            // never enters mostly writes entries the next search never looks up.
            //
            // The alternative was to hide the root from the repetition test, which would
            // leave the ponder's stored scores carrying no claim about the predicted move
            // at all. It was not taken. It costs the ponder its own defence against
            // shuffling — it would think returning to its own root is free, which is the
            // one thing the -25 exists to stop — and it means changing SearchEngine to
            // remove an error the measurement could not find. A duller search everywhere
            // is a poor price for a rarer error in the subtree that gets abandoned.
            if (PredictedReply(state) is { } reply)
            {
                GameState after = state;
                after.Apply(reply);

                // A predicted move that wins the game has no subtree worth filling, and
                // the search would spend the whole budget storing mate scores for a
                // position nobody will ever reach. Fall back rather than waste it. A
                // predicted move that picks up a free move and hands the opponent another
                // one needs no such care: the root is simply a position they are still on
                // move in, one ply closer to the search that follows than the parent was.
                if (!after.IsGameOver)
                {
                    // The caller's history stops before the position it hands over, so
                    // the parent has to be appended by hand. It is the last position of
                    // the real game, which is what keeps every index below the root exact.
                    extended = new ulong[positionHistory.Length + 1];
                    positionHistory.CopyTo(extended);
                    extended[^1] = state.Hash;

                    root = after;
                    history = extended;
                }
            }

            _ponderEngine.Search(
                root, _maxDepth, Stopwatch.StartNew(), PonderBudget, history, token);
        }
        finally
        {
            Volatile.Write(ref _pondering, 0);
        }
    }

    /// <summary>
    /// The move the engine expects the opponent to play in <paramref name="state"/>, or
    /// null when it has no expectation.
    ///
    /// There is no principal variation array to read this out of — a
    /// <see cref="SearchResult"/> is a move, a score, a depth and a node count. But a
    /// two-ply line is recoverable anyway: the root of a search is the one node
    /// <see cref="SearchEngine"/> never stores, while every interior node below it is
    /// stored with its best move. <paramref name="state"/> is the position after the
    /// engine's own move, so it is an interior node of the search that just chose that
    /// move, and the move sitting in the table there is the reply that search expected.
    /// Reading it costs one probe and needs nothing added to the search.
    ///
    /// The entry may be from an older search, or from a ponder, or from a different
    /// position entirely under a hash collision — the table is shared, lock-free and
    /// indexed by a truncated key. So the move is put through
    /// <see cref="MoveCandidates.IsLegal"/> exactly as <see cref="SearchEngine"/> does
    /// with every table move it orders on, and an expectation that fails that check is
    /// simply not an expectation.
    ///
    /// Having none is rare and <see cref="Ponder"/> falls back to the parent when it
    /// happens: 9 of 210 opponent replies against the heuristic and 21 of 418 against an
    /// engine, which is the first ponder of a game where the human moves first, plus the
    /// occasional entry evicted before it could be read.
    /// </summary>
    private Move? PredictedReply(in GameState state)
    {
        if (!_table.TryGet(state.Hash, out TableEntry entry) || !entry.HasMove) return null;

        return MoveCandidates.IsLegal(state, entry.Move) ? entry.Move : null;
    }

    private SearchResult SearchInParallel(GameState root, ulong[] history, Stopwatch clock, CancellationToken cancellationToken)
    {
        using var helpers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken helperToken = helpers.Token;

        var running = new Task[_engines.Length - 1];

        for (int i = 1; i < _engines.Length; i++)
        {
            SearchEngine engine = _engines[i];

            // Staggered starting depths keep the helpers off the main thread's exact
            // path, which is where lazy SMP gets its extra coverage.
            int startDepth = 1 + (i & 1);

            running[i - 1] = Task.Run(
                () => engine.Search(root, _maxDepth, clock, MoveTime, history, helperToken, startDepth),
                helperToken);
        }

        SearchResult result = _engines[0].Search(root, _maxDepth, clock, MoveTime, history, cancellationToken);

        helpers.Cancel();

        try
        {
            Task.WaitAll(running);
        }
        catch (AggregateException)
        {
            // Helpers were cancelled on purpose; their results were never needed.
        }

        return result;
    }
}

