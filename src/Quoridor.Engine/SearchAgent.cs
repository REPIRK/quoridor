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
    private readonly TranspositionTable _table;
    private readonly SearchEngine[] _engines;
    private readonly int _maxDepth;

    private ulong[] _positionHistory = Array.Empty<ulong>();

    public SearchAgent(
        int maxDepth = 32,
        TimeSpan? moveTime = null,
        int threads = 1,
        int tableMegabytes = 64,
        EvaluationWeights? weights = null,
        EngineOptions? options = null)
    {
        _maxDepth = Math.Clamp(maxDepth, 1, SearchEngine.MaxPly - 4);
        DefaultMoveTime = moveTime ?? TimeSpan.FromMilliseconds(1000);
        MoveTime = DefaultMoveTime;
        _table = new TranspositionTable(tableMegabytes);

        int count = Math.Clamp(threads, 1, Math.Max(1, Environment.ProcessorCount));
        _engines = new SearchEngine[count];
        for (int i = 0; i < count; i++) _engines[i] = new SearchEngine(_table, weights, options, threadIndex: i);
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

