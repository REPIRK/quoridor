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
    /// Searches <paramref name="state"/> for as long as the token allows, purely to fill
    /// the shared table for the search that will follow. Meant for the opponent's own
    /// position while they are deciding what to do with it: whatever they play, the game
    /// descends into a subtree this has already been through, so there is no prediction
    /// to get wrong and nothing to restart on a miss.
    ///
    /// It returns nothing on purpose. There is no result here that could reach the board
    /// because there is no result at all — nothing is written to <see cref="LastResult"/>,
    /// nothing to <see cref="MoveTime"/>, nothing to the stored game history, and no new
    /// table generation is opened. All of those belong to the move actually being played.
    ///
    /// The history is a parameter rather than a call to <see cref="SetGameHistory"/>
    /// because that method replaces the array <see cref="ChooseMove"/> reads, and a
    /// ponder has no business touching what the next real search will be given.
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
            _ponderEngine.Search(
                state, _maxDepth, Stopwatch.StartNew(), PonderBudget, positionHistory, token);
        }
        finally
        {
            Volatile.Write(ref _pondering, 0);
        }
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

