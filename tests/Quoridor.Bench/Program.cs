using System.Diagnostics;
using System.Runtime.InteropServices;
using Quoridor.Core;
using Quoridor.Engine;

namespace Quoridor.Bench;

/// <summary>
/// Measures the engine rather than testing it. Three questions:
/// how deep does it get, does thinking longer actually make it play better, and does
/// adding threads buy anything.
///
///   dotnet run -c Release --project tests/Quoridor.Bench -- [depth|ladder|smp|portals|all]
///
/// And a fourth, since the engine learned to search while the opponent is deciding:
/// is that worth anything. <c>ponder</c> asks whether the work survives at all,
/// <c>ponderhit</c> asks how much of it lands where the game actually goes and how often
/// the opponent plays the move the engine expected, and <c>ponderduel</c> asks the only
/// question that settles it, which is whether it wins games.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "all";

        Console.WriteLine($"{Environment.ProcessorCount} logical cores, {(Environment.Is64BitProcess ? "x64" : "x86")}");
        Console.WriteLine();

        Warmup();

        if (mode is "all" or "depth") DepthBenchmark();
        if (mode is "all" or "ladder") TimeLadder();
        if (mode is "all" or "smp") ThreadScaling();
        if (mode is "trace") Trace();
        if (mode is "fixed") FixedDepthLadder();
        if (mode is "tune") TuneWallWeight();
        if (mode is "ablate") Ablate();
        if (mode is "race") SweepRaceVerdict();
        if (mode is "duel") Duel();
        if (mode is "pickups") PickupDuel();
        if (mode is "holes") HoleDuel();
        if (mode is "portals") PortalCost();
        if (mode is "smpduel") ThreadDuel();
        if (mode is "ponder") PonderMechanism(args);
        if (mode is "ponderhit") PonderPrediction(args);
        if (mode is "ponderduel") PonderDuel(args);

        return 0;
    }

    /// <summary>
    /// A search thrown away before any are counted. The runtime starts every method in
    /// an unoptimised tier and only recompiles it once it has been called enough, so the
    /// first search in a process runs at a fraction of the speed of the ones after it —
    /// enough that the very first row of a timed table came out several plies shallower
    /// than the identical position measured later. That is a property of the process and
    /// not of the engine, so it is paid for here where it costs one line instead of
    /// being charged to whichever measurement happened to run first.
    /// </summary>
    private static void Warmup()
    {
        GameState state = GameState.CreateInitial();
        foreach (string text in new[] { "e2", "e8", "e3", "e7", "e6h", "d7" })
        {
            if (Notation.TryParse(text, out Move move) && state.IsLegal(move)) state.Apply(move);
        }

        new SearchAgent(maxDepth: 40, moveTime: TimeSpan.FromMilliseconds(700), threads: 1)
            .ChooseMove(state);
    }

    // ================================================================= ponder ==

    /// <summary>
    /// Does a ponder leave anything behind at all? Not a game — a single search, run
    /// twice from the same position, once after the engine has spent time on the
    /// position one ply earlier and once cold. Everything else is held still: same
    /// agent settings, same real budget, same history.
    ///
    /// The gain, if there is one, has to show up as depth going up or as nodes to the
    /// same depth coming down, because the only thing a ponder can hand over is
    /// transposition table entries — killers, history and the aspiration seed all die
    /// with the ponder engine when its search returns. If neither number moves at eight
    /// seconds of pondering, the table is not being reused and there is a bug to find
    /// before anyone spends an hour on duels.
    /// </summary>
    private static void PonderMechanism(string[] args)
    {
        var real = TimeSpan.FromMilliseconds(300);
        int[] ponders = { 500, 2000, 8000 };

        // Switching pondering on in the app also quadruples the table, on the argument
        // that a long ponder would otherwise fill a 64 MB one and start evicting its own
        // work. That is two changes wearing one switch, so the size is an argument here.
        TableMegabytes = args.Length > 1 ? int.Parse(args[1]) : 256;

        Console.WriteLine(
            $"Ponder mechanism: real search {real.TotalMilliseconds:F0} ms, single thread, " +
            $"{TableMegabytes} MB table");
        Console.WriteLine();
        Console.WriteLine("  \"parent\" is the position the design ponders — the one the opponent is");
        Console.WriteLine("  deciding from. \"child\" ponders the position the real search will actually");
        Console.WriteLine("  run on, which no design can arrange but which bounds what any of them can");
        Console.WriteLine("  be worth: it is a prediction that is right every single time.");
        Console.WriteLine();
        Console.WriteLine("  position         ponder   root     to    real   nodes   move    score");

        var fromParent = new List<int>();
        var fromChild = new List<int>();

        foreach ((string name, GameState parent, GameState child, ulong[] history) in PonderPositions())
        {
            ReadOnlySpan<ulong> parentHistory = history.AsSpan(0, history.Length - 1);

            (int coldDepth, long coldNodes, Move coldMove, int coldScore) =
                Answer(child, history, real, default, default, 0);

            Console.WriteLine(
                $"  {name,-16} {"cold",6}   {"",-6} {"",4} {coldDepth,6} {coldNodes,10:N0}   " +
                $"{Notation.Format(coldMove),-6} {coldScore,6}");

            foreach (int ponderMs in ponders)
            {
                foreach ((string where, GameState root, ulong[] rootHistory) in new[]
                         {
                             ("parent", parent, parentHistory.ToArray()),
                             ("child ", child, history),
                         })
                {
                    (int depth, long nodes, Move move, int score) =
                        Answer(child, history, real, root, rootHistory, ponderMs);

                    (where == "parent" ? fromParent : fromChild).Add(depth - coldDepth);

                    Console.WriteLine(
                        $"  {name,-16} {ponderMs,6} {where,-6} {PonderDepth,4} {depth,6} {nodes,10:N0}   " +
                        $"{Notation.Format(move),-6} {score,6}");
                }
            }

            Console.WriteLine();
        }

        Summarise("  pondering the parent, as designed", fromParent);
        Summarise("  pondering the child, the ceiling ", fromChild);
        Console.WriteLine();

        static void Summarise(string label, List<int> gains)
        {
            if (gains.Count == 0) return;

            Console.WriteLine(
                $"{label}: n={gains.Count}, mean {gains.Average():+0.00;-0.00;0.00} ply, " +
                $"{gains.Count(g => g > 0)} deeper / {gains.Count(g => g < 0)} shallower");
        }
    }

    /// <summary>How deep the last ponder run by <see cref="Answer"/> managed to get.</summary>
    private static int PonderDepth;

    /// <summary>The table both engines in <see cref="Answer"/> share.</summary>
    private static int TableMegabytes = 256;

    /// <summary>
    /// One real search on <paramref name="child"/>, optionally preceded by a ponder on
    /// <paramref name="ponderRoot"/> over the same table.
    ///
    /// A table and two engines rather than a <see cref="SearchAgent"/>, so the ponder's
    /// own depth can be read as well as the real search's — the agent's ponder returns
    /// nothing, on purpose, and the agent's table is private. The calls below are the
    /// agent's calls: a new generation and a budgeted search for the real move, and for
    /// the ponder a separate engine over the same table with no new generation.
    /// </summary>
    private static (int Depth, long Nodes, Move Move, int Score) Answer(
        in GameState child,
        ulong[] childHistory,
        TimeSpan real,
        in GameState ponderRoot,
        ulong[]? ponderHistory,
        int ponderMs)
    {
        var table = new TranspositionTable(TableMegabytes);
        PonderDepth = 0;

        if (ponderMs > 0 && ponderHistory is not null)
        {
            // On this thread rather than a background one. Production cancels a ponder
            // mid-node and this one stops on its own budget, but a stopped node stores
            // nothing either way, so the table ends up the same and the scheduler is
            // taken out of the measurement.
            using var stop = new CancellationTokenSource(ponderMs);

            SearchResult pondered = new SearchEngine(table).Search(
                ponderRoot, 40, Stopwatch.StartNew(), TimeSpan.FromSeconds(15),
                ponderHistory, stop.Token);

            PonderDepth = pondered.Depth;
        }

        // The agent opens a generation for every move it is asked to play and never for
        // a ponder, which is what leaves the ponder's entries readable but with no claim
        // on their slots. Faithful here because that claim is exactly what decides
        // whether the real search keeps them or writes over them.
        table.NewGeneration();

        SearchResult result = new SearchEngine(table).Search(
            child, 40, Stopwatch.StartNew(), real, childHistory, CancellationToken.None);

        return (result.Depth, result.Nodes, result.Move, result.Score);
    }

    /// <summary>
    /// The number that decides whether this design pondered the right position. It
    /// ponders the opponent's own position, so there is no prediction to get wrong —
    /// but the alternative, pondering the reply the engine expects, is only the better
    /// bet if that reply is usually what gets played. So: after every engine move, ask
    /// the table what it expects next, and see how often the opponent obliges.
    ///
    /// Measured against a paired reading of what the ponder was actually worth at that
    /// same position, so the two can be crossed: if the gain is concentrated on the
    /// plies where the prediction was right, the prediction variant is worth revisiting,
    /// and if it is spread evenly it is not.
    ///
    /// A bench-local engine and table rather than a <see cref="SearchAgent"/>, because
    /// the agent owns its table privately and the whole measurement is a probe of it.
    /// The calls are the same ones the agent makes.
    /// </summary>
    private static void PonderPrediction(string[] args)
    {
        int ponderMs = args.Length > 1 ? int.Parse(args[1]) : 3000;
        int games = args.Length > 2 ? int.Parse(args[2]) : 6;
        bool strong = args.Length <= 3 || args[3] != "heuristic";
        var real = TimeSpan.FromMilliseconds(300);

        Console.WriteLine(
            $"Prediction and gain over {games} games, {ponderMs} ms ponder against a " +
            $"{real.TotalMilliseconds:F0} ms search, opponent {(strong ? "engine" : "heuristic")}");

        int predicted = 0, hits = 0, replies = 0;
        var gainOnHit = new List<int>();
        var gainOnMiss = new List<int>();
        var nodesOnHit = new List<double>();
        var nodesOnMiss = new List<double>();
        var ceiling = new List<int>();

        // The paired measurement below costs a ponder and two real searches, so it runs
        // on every fourth reply rather than all of them. The prediction itself is a
        // single probe and is taken on every one, which is why the two sample sizes
        // printed at the end are different numbers.
        const int measureEvery = 4;

        for (int game = 0; game < games; game++)
        {
            // The engine side is a table and an engine driven directly rather than a
            // SearchAgent, for the single reason that the agent owns its table privately
            // and the prediction being measured lives in it. The calls below are the
            // agent's calls, in the order it makes them.
            var table = new TranspositionTable(64);
            var reference = new SearchEngine(table);

            // A second engine rather than the heuristic stands in for the human, because
            // the heuristic loses in seventeen plies and a game that short measures the
            // opening and nothing else. It is also the harder test of a prediction: an
            // opponent whose replies are reasonable is one whose replies the engine has
            // some hope of guessing, so this is the hit rate at its most flattering.
            var opponentTable = new TranspositionTable(64);
            var opponent = new SearchEngine(opponentTable);
            IQuoridorAgent weak = new HeuristicAgent(BotStrength.Normal, seed: 7 + game);

            int engineSeat = game % 2;
            GameState state = RandomOpening(new Random(2000 + game), plies: 4);
            var history = new List<ulong>();

            for (int ply = 0; ply < 300 && !state.IsGameOver; ply++)
            {
                if (state.SideToMove == engineSeat)
                {
                    table.NewGeneration();

                    SearchResult found = reference.Search(
                        state, 40, Stopwatch.StartNew(), real,
                        CollectionsMarshal.AsSpan(history), CancellationToken.None);

                    Move played = state.IsLegal(found.Move) ? found.Move : HeuristicAgent.Fallback(state);

                    history.Add(state.Hash);
                    state.Apply(played);
                    continue;
                }

                // The opponent is on move, so this is the position a ponder would run on
                // and the position the engine has just been asked to predict. The
                // expected reply is whatever the engine's last search left in the table
                // here: the root of that search is never stored, but every node below it
                // is, and this position is one of them.
                Move expected = default;

                if (table.TryGet(state.Hash, out TableEntry entry) && entry.HasMove
                    && state.IsLegal(entry.Move))
                {
                    expected = entry.Move;
                }

                Move actual;

                if (strong)
                {
                    opponentTable.NewGeneration();

                    SearchResult reply = opponent.Search(
                        state, 40, Stopwatch.StartNew(), real,
                        CollectionsMarshal.AsSpan(history), CancellationToken.None);

                    actual = state.IsLegal(reply.Move) ? reply.Move : HeuristicAgent.Fallback(state);
                }
                else
                {
                    weak.SetGameHistory(CollectionsMarshal.AsSpan(history));
                    actual = weak.ChooseMove(state);
                }

                bool hit = expected != default && expected == actual;

                if (expected != default)
                {
                    predicted++;
                    if (hit) hits++;
                }

                if (replies++ % measureEvery == 0)
                {
                    // What the ponder was worth right here, measured in pairs: the same
                    // real search from the same position, once warmed by a ponder on the
                    // position the opponent was deciding from and once stone cold.
                    GameState after = state;
                    after.Apply(actual);

                    var extended = new List<ulong>(history) { state.Hash };
                    ulong[] childHistory = extended.ToArray();

                    (int cold, long coldNodes, _, _) =
                        Answer(after, childHistory, real, default, null, 0);
                    (int warm, long warmNodes, _, _) =
                        Answer(after, childHistory, real, state, history.ToArray(), ponderMs);

                    // And the ceiling, on the very same position: a ponder that spent
                    // the whole of the human's time on the position the human was about
                    // to create. No design can arrange that, which is the point — it is
                    // the most any amount of pondering could possibly be worth here.
                    (int perfect, _, _, _) =
                        Answer(after, childHistory, real, after, childHistory, ponderMs);

                    ceiling.Add(perfect - cold);

                    double ratio = coldNodes > 0 ? (double)warmNodes / coldNodes : 1;

                    if (hit) { gainOnHit.Add(warm - cold); nodesOnHit.Add(ratio); }
                    else { gainOnMiss.Add(warm - cold); nodesOnMiss.Add(ratio); }
                }

                history.Add(state.Hash);
                state.Apply(actual);
            }
        }

        Console.WriteLine(
            $"  {replies} opponent replies over {games} games; the engine held an expected reply for " +
            $"{predicted} of them and the opponent played it {hits} times " +
            $"({(predicted > 0 ? 100.0 * hits / predicted : 0):F0}%)");

        Report("  gain when the reply was predicted", gainOnHit, nodesOnHit);
        Report("  gain when it was not             ", gainOnMiss, nodesOnMiss);
        Report("  gain overall                     ", gainOnHit.Concat(gainOnMiss).ToList(),
               nodesOnHit.Concat(nodesOnMiss).ToList());

        if (ceiling.Count > 0)
        {
            Console.WriteLine(
                $"  ceiling, same positions           n={ceiling.Count,3}, depth {ceiling.Average():+0.00;-0.00;0.00} ply, " +
                $"{ceiling.Count(g => g > 0)} deeper / {ceiling.Count(g => g < 0)} shallower");
        }

        Console.WriteLine();

        static void Report(string label, List<int> gains, List<double> ratios)
        {
            if (gains.Count == 0)
            {
                Console.WriteLine($"{label} no samples");
                return;
            }

            Console.WriteLine(
                $"{label} n={gains.Count,3}, depth {gains.Average():+0.00;-0.00;0.00} ply, " +
                $"nodes {ratios.Average():P0} of cold, {gains.Count(g => g > 0)} deeper / " +
                $"{gains.Count(g => g < 0)} shallower");
        }
    }

    /// <summary>
    /// The headline. Depth on a handful of positions says the mechanism works; this says
    /// whether it wins games, which is the only currency the rest of this file is
    /// written in.
    ///
    /// One side is given a fixed ponder on every position its opponent is deciding from,
    /// standing in for a human who thinks that long about every move. The other side is
    /// the engine exactly as it ships today. Run it against a plain engine on a longer
    /// clock as well and the answer comes out in the same units as the time ladder:
    /// pondering for this long is worth playing at that many milliseconds.
    ///
    ///   ponderduel [games] [ponderMs] [budgetA] [budgetB] [tableA] [tableB]
    ///
    /// The table sizes are arguments and not constants because the app ties them to the
    /// feature — switching pondering on also quadruples the table — and those are two
    /// changes, not one. A ponder of zero with the two sizes left different is the
    /// control that separates them: side A gets everything pondering brings except the
    /// pondering. If that alone moves the score, the duel is measuring the table.
    /// </summary>
    private static void PonderDuel(string[] args)
    {
        int games = args.Length > 1 ? int.Parse(args[1]) : 24;
        int ponderMs = args.Length > 2 ? int.Parse(args[2]) : 3000;
        int budgetA = args.Length > 3 ? int.Parse(args[3]) : 300;
        int budgetB = args.Length > 4 ? int.Parse(args[4]) : 300;
        int tableA = args.Length > 5 ? int.Parse(args[5]) : 256;
        int tableB = args.Length > 6 ? int.Parse(args[6]) : 64;

        // Twenty-four games is this project's floor for believing a result, and an edge
        // of half a ply needs far more than that. The offset moves the openings so
        // several runs can be pooled into one sample instead of being one experiment
        // repeated on the same twenty-four positions.
        int seed = args.Length > 7 ? int.Parse(args[7]) : 0;

        Console.WriteLine(
            $"Ponder duel, {games} games: {ponderMs} ms of pondering at {budgetA} ms per move over a " +
            $"{tableA} MB table, against a plain engine at {budgetB} ms per move over {tableB} MB");

        Play($"ponder {ponderMs} ms @ {budgetA} ms, {tableA} MB",
             () => new SearchAgent(
                 maxDepth: 40, moveTime: TimeSpan.FromMilliseconds(budgetA), threads: 1,
                 tableMegabytes: tableA, ponder: true),
             $"plain @ {budgetB} ms, {tableB} MB",
             () => new SearchAgent(
                 maxDepth: 40, moveTime: TimeSpan.FromMilliseconds(budgetB), threads: 1,
                 tableMegabytes: tableB),
             games,
             (_, game) => RandomOpening(new Random(1000 + seed + game), plies: 4),
             ponderMs);

        Console.WriteLine();
    }

    private static IEnumerable<(string Name, GameState Parent, GameState Child, ulong[] History)> PonderPositions()
    {
        yield return Line("middlegame", GameState.CreateInitial(),
            "e2", "e8", "e3", "e7", "e6h", "d7", "e4", "d6", "d4v", "c6");

        yield return Line("holes", new GameSetup { Holes = 8, Seed = 700 }.Build().State,
            "e2", "e8", "e3", "e7", "e4", "e6");

        yield return Line("portals", new GameSetup { Portals = 2, Seed = 19 }.Build().State,
            "e2", "e8", "e3", "e7", "e4", "e6");

        yield return Line("pickups", new GameSetup { Pickups = 8, Seed = 500 }.Build().State,
            "e2", "e8", "e3", "e7");

        // A longer, wallier line: the ones above are still mostly races, and a position
        // with walls on it is where a ponder has the most to find. The extra walls are
        // taken from the move generator rather than written out, so the line cannot go
        // stale the next time the board grows a feature — the same reason the openings
        // the duels use are generated too.
        yield return Line("walled", GameState.CreateInitial(),
            Extend(new[] { "e2", "e8", "e3", "e7", "e6h", "d7", "e4", "d6" }, walls: 4));

        static string[] Extend(string[] opening, int walls)
        {
            GameState state = GameState.CreateInitial();
            var line = new List<string>();

            foreach (string text in opening)
            {
                Notation.TryParse(text, out Move move);
                state.Apply(move);
                line.Add(text);
            }

            for (int i = 0; i < walls; i++)
            {
                var legal = state.LegalMoves();
                Move chosen = legal.FirstOrDefault(m => m.IsWall, legal[0]);

                state.Apply(chosen);
                line.Add(Notation.Format(chosen));
            }

            return line.ToArray();
        }

        static (string, GameState, GameState, ulong[]) Line(string name, GameState start, params string[] moves)
        {
            GameState state = start;
            var history = new List<ulong>();

            foreach (string text in moves)
            {
                if (!Notation.TryParse(text, out Move move) || !state.IsLegal(move))
                    throw new InvalidOperationException($"bad ponder line at {text} in {name}");

                history.Add(state.Hash);
                state.Apply(move);
            }

            // The parent is the position the ponder runs on and the child is the one the
            // real search answers from, so the history handed over is the child's — the
            // parent's is the same array one shorter, which is the point.
            GameState parent = default;
            {
                GameState replay = start;
                for (int i = 0; i < moves.Length - 1; i++)
                {
                    Notation.TryParse(moves[i], out Move move);
                    replay.Apply(move);
                }

                parent = replay;
            }

            return (name, parent, state, history.ToArray());
        }
    }

    /// <summary>
    /// Threads against no threads at the same clock. Depth on a handful of positions is
    /// suggestive; this is the number that decides whether SMP earns its cores.
    /// </summary>
    private static void ThreadDuel()
    {
        const int games = 20;
        var clock = TimeSpan.FromMilliseconds(300);

        int many = Math.Clamp(Environment.ProcessorCount / 3, 2, 8);
        Console.WriteLine($"Thread duel at {clock.TotalMilliseconds:F0} ms per move, {games} games");

        Play($"{many} threads", () => new SearchAgent(maxDepth: 40, moveTime: clock, threads: many),
             "1 thread", () => new SearchAgent(maxDepth: 40, moveTime: clock, threads: 1),
             games);

        Console.WriteLine();
    }

    /// <summary>
    /// Weight sets against each other at identical depth. This is the only comparison
    /// that isolates evaluation quality — a sweep against a shallower reference mixes
    /// it up with how well the search scales.
    /// </summary>
    private static void Duel()
    {
        const int games = 24;
        const int depth = 5;
        var generous = TimeSpan.FromSeconds(30);

        Console.WriteLine($"Equal-depth duels, depth {depth}, {games} games each");

        IQuoridorAgent Make(EngineOptions? options = null, EvaluationWeights? weights = null) =>
            new SearchAgent(
                maxDepth: depth, moveTime: generous, threads: 1,
                weights: weights ?? EvaluationWeights.Default,
                options: options ?? EngineOptions.Default);

        Play("history off  ", () => Make(EngineOptions.Default with { UseHistoryOrdering = false }),
             "current default", () => Make(),
             games);

        Console.WriteLine();
    }

    /// <summary>
    /// How much a pickup should pull, decided the only honest way: the same engine at
    /// the same depth, on boards with pickups, with the term on against it off.
    /// </summary>
    private static void PickupDuel()
    {
        const int games = 40;
        const int depth = 5;
        var generous = TimeSpan.FromSeconds(30);

        Console.WriteLine($"Pickup boards, equal depth {depth}, {games} games each");

        foreach (int value in new[] { 12, 25, 40 })
        {
            EvaluationWeights aware = EvaluationWeights.Default with { Pickup = value };
            EvaluationWeights blind = EvaluationWeights.Default with { Pickup = 0 };

            Play($"pickup={value,-3}",
                 () => new SearchAgent(maxDepth: depth, moveTime: generous, threads: 1, weights: aware),
                 "blind",
                 () => new SearchAgent(maxDepth: depth, moveTime: generous, threads: 1, weights: blind),
                 games,
                 (random, game) => PickupOpening(random, seed: 500 + game, plies: 4));
        }

        Console.WriteLine();
    }

    /// <summary>
    /// A hole makes the board narrower, so the guess was that a wall shuts more down
    /// there and should be priced higher. It does not: 240 and 300 both lost 11:29, and
    /// 120 and 150 were a wash. The default is right on these boards too, and this mode
    /// is kept so the finding can be re-run rather than taken on trust.
    /// </summary>
    private static void HoleDuel()
    {
        const int games = 40;
        const int depth = 5;
        var generous = TimeSpan.FromSeconds(30);

        Console.WriteLine($"Hole boards, equal depth {depth}, {games} games each");

        foreach (int wall in new[] { 120, 150, 240 })
        {
            EvaluationWeights tried = EvaluationWeights.Default with { Wall = wall };

            Play($"wall={wall,-3}",
                 () => new SearchAgent(maxDepth: depth, moveTime: generous, threads: 1, weights: tried),
                 $"wall={EvaluationWeights.Default.Wall}",
                 () => new SearchAgent(maxDepth: depth, moveTime: generous, threads: 1),
                 games,
                 (random, game) => HoleOpening(random, seed: 700 + game, plies: 4));
        }

        Console.WriteLine();
    }

    /// <summary>
    /// What portals cost the search, kept as its own number and never folded into an
    /// average with anything else.
    ///
    /// A portal is an ordinary undirected edge, so the flood fill has to follow it: one
    /// AND against the mouths that have not fired yet at every breadth-first step, plus a
    /// short loop per portal per fill. Wall legality runs that fill twice for each of
    /// about thirty candidate walls at every node, so the whole cost lands on nodes per
    /// second — a portal game simply searches a slower board. Both numbers are printed
    /// side by side rather than one blended one, because a single averaged figure would
    /// hide the price of the feature in the boards that do not have it.
    ///
    /// Matched pairs: the same four-ply random opening is played on a plain board and on a
    /// portal board built from the same seed, so the only thing that differs is the edges.
    /// </summary>
    private static void PortalCost()
    {
        const int milliseconds = 1000;

        Console.WriteLine($"Portal cost, {milliseconds} ms per search, single thread");
        Console.WriteLine("  board               depth      nodes        nodes/s");

        double plainTotal = 0, portalTotal = 0;
        int pairs = 0;

        foreach (int seed in new[] { 19, 20, 26 })
        {
            double plain = Measure($"plain seed {seed}", RandomOpening(new Random(seed), plies: 4));
            double warped = Measure($"portals seed {seed}", PortalOpening(new Random(seed), seed, plies: 4));

            plainTotal += plain;
            portalTotal += warped;
            pairs++;
        }

        if (pairs > 0 && plainTotal > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  a portal board searched {100 * (plainTotal - portalTotal) / plainTotal:F0}% " +
                              "fewer nodes per second than the plain board beside it");
        }

        Console.WriteLine();

        static double Measure(string name, GameState state)
        {
            var agent = new SearchAgent(maxDepth: 40, moveTime: TimeSpan.FromMilliseconds(milliseconds), threads: 1);

            var clock = Stopwatch.StartNew();
            agent.ChooseMove(state);
            clock.Stop();

            SearchResult result = agent.LastResult;
            double nps = result.Nodes / Math.Max(0.001, clock.Elapsed.TotalSeconds);

            Console.WriteLine($"  {name,-18} {result.Depth,7} {result.Nodes,10:N0} {nps,14:N0}");
            return nps;
        }
    }

    private static void SweepRaceVerdict()
    {
        const int games = 10;
        const int deep = 6;
        var generous = TimeSpan.FromSeconds(30);

        Console.WriteLine($"Race verdict sweep, depth {deep} against depth 3, {games} games each");

        foreach (int verdict in new[] { 0, 50, 120, 240 })
        {
            EvaluationWeights weights = EvaluationWeights.Default with { RaceVerdict = verdict };

            Play($"verdict={verdict,-3}",
                 () => new SearchAgent(maxDepth: deep, moveTime: generous, threads: 1, weights: weights),
                 "depth 3",
                 () => new SearchAgent(maxDepth: 3, moveTime: generous, threads: 1, weights: weights),
                 games);
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Strength stopped growing past about four plies, so something in the search is
    /// paying for its depth with accuracy. Turn each technique off in turn and see
    /// which one is responsible.
    /// </summary>
    private static void Ablate()
    {
        const int games = 8;
        const int deep = 6;
        var generous = TimeSpan.FromSeconds(30);

        Console.WriteLine($"Ablation: depth {deep} against a fixed depth-3 reference, {games} games each");

        IQuoridorAgent Reference() => new SearchAgent(maxDepth: 3, moveTime: generous, threads: 1);

        (string Label, EngineOptions Options, EvaluationWeights Weights)[] variants =
        {
            ("everything on", EngineOptions.Default, EvaluationWeights.Default),
            ("no LMR", EngineOptions.Default with { UseLateMoveReductions = false }, EvaluationWeights.Default),
            ("no table", EngineOptions.Default with { UseTranspositionTable = false }, EvaluationWeights.Default),
            ("no aspiration", EngineOptions.Default with { UseAspirationWindows = false }, EvaluationWeights.Default),
            ("wall scores everywhere", EngineOptions.Default with { ScoreWallsEverywhere = true }, EvaluationWeights.Default),
            ("no race verdict", EngineOptions.Default, EvaluationWeights.Default with { RaceVerdict = 0 }),
        };

        foreach ((string label, EngineOptions options, EvaluationWeights weights) in variants)
        {
            Play($"depth {deep}, {label,-22}",
                 () => new SearchAgent(maxDepth: deep, moveTime: generous, threads: 1, weights: weights, options: options),
                 "depth 3",
                 Reference,
                 games);
        }

        Console.WriteLine();
    }

    /// <summary>
    /// How much is an unspent wall worth? Play the candidates against a fixed
    /// reference rather than guessing, at a depth low enough to run many games.
    /// </summary>
    private static void TuneWallWeight()
    {
        const int games = 16;
        const int depth = 4;
        var generous = TimeSpan.FromSeconds(30);

        Console.WriteLine($"Wall weight sweep, depth {depth}, {games} games each");

        foreach (int wall in new[] { 34, 80, 120, 170, 220 })
        {
            EvaluationWeights weights = EvaluationWeights.Default with { Wall = wall };

            Play($"wall={wall,-3}", () => new SearchAgent(maxDepth: depth, moveTime: generous, threads: 1, weights: weights),
                 "heuristic", () => new HeuristicAgent(BotStrength.Normal, seed: 7),
                 games);
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Depth against depth with time taken out of the picture. If a deeper search does
    /// not win here, the problem is the search itself rather than time management.
    /// </summary>
    private static void FixedDepthLadder()
    {
        const int games = 24;
        var generous = TimeSpan.FromSeconds(30);

        Console.WriteLine($"Fixed depth, {games} games");

        foreach ((int shallow, int deep) in new[] { (2, 4), (4, 6) })
        {
            Play($"depth {deep}", () => new SearchAgent(maxDepth: deep, moveTime: generous, threads: 1),
                 $"depth {shallow}", () => new SearchAgent(maxDepth: shallow, moveTime: generous, threads: 1),
                 games);
        }

        Play("depth 4", () => new SearchAgent(maxDepth: 4, moveTime: generous, threads: 1),
             "heuristic", () => new HeuristicAgent(BotStrength.Normal, seed: 7),
             games);

        Console.WriteLine();
    }

    /// <summary>Plays one game move by move with the engine's reasoning printed.</summary>
    private static void Trace()
    {
        var engine = new SearchAgent(maxDepth: 40, moveTime: TimeSpan.FromMilliseconds(400), threads: 1);
        var opponent = new HeuristicAgent(BotStrength.Normal, seed: 3);

        const int enginePlayer = 0;
        GameState state = GameState.CreateInitial();
        var history = new List<ulong>();

        Console.WriteLine("ply  side  move    depth  score      nodes   d(P1)  d(P2)");

        for (int ply = 0; ply < 160 && !state.IsGameOver; ply++)
        {
            bool engineTurn = state.SideToMove == enginePlayer;
            IQuoridorAgent agent = engineTurn ? engine : opponent;

            agent.SetGameHistory(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(history));
            Move move = agent.ChooseMove(state);

            history.Add(state.Hash);
            state.Apply(move);

            int d0 = PathFinder.Distance(state, 0);
            int d1 = PathFinder.Distance(state, 1);

            string detail = engineTurn
                ? $"{engine.LastResult.Depth,5} {engine.LastResult.Score,6} {engine.LastResult.Nodes,10:N0}"
                : $"{"",5} {"",6} {"",10}";

            Console.WriteLine($"{ply,3}  {(engineTurn ? "ENG" : "heu"),4}  {Notation.Format(move),-6} {detail}  {d0,5}  {d1,5}");
        }

        Console.WriteLine(state.IsGameOver
            ? $"winner: {(state.Winner == enginePlayer ? "ENGINE" : "heuristic")}"
            : "unfinished");
    }

    // ================================================================== depth ==

    private static void DepthBenchmark()
    {
        Console.WriteLine("Search depth reached, single thread");
        Console.WriteLine("  position          time     depth      nodes        nodes/s   move   score");

        foreach ((string name, GameState state) in Positions())
        {
            foreach (int milliseconds in new[] { 100, 250, 1000 })
            {
                var agent = new SearchAgent(maxDepth: 40, moveTime: TimeSpan.FromMilliseconds(milliseconds), threads: 1);

                var clock = Stopwatch.StartNew();
                Move move = agent.ChooseMove(state);
                clock.Stop();

                SearchResult result = agent.LastResult;
                double nps = result.Nodes / Math.Max(0.001, clock.Elapsed.TotalSeconds);

                Console.WriteLine(
                    $"  {name,-16} {milliseconds,5} ms {result.Depth,7} {result.Nodes,10:N0} {nps,14:N0}   " +
                    $"{Notation.Format(move),-6} {result.Score,6}");
            }
        }

        Console.WriteLine();
    }

    // ================================================================= ladder ==

    /// <summary>
    /// The honest test of a search: give one side four times the thinking time. If the
    /// extra depth is real, the slower side should win comfortably. If the score comes
    /// out level, the search is not converting depth into strength.
    /// </summary>
    private static void TimeLadder()
    {
        const int games = 16;

        Console.WriteLine($"Time ladder, {games} games with randomised openings");

        Play("engine 1500 ms", () => new SearchAgent(maxDepth: 40, moveTime: TimeSpan.FromMilliseconds(1500), threads: 1),
             "engine 50 ms", () => new SearchAgent(maxDepth: 40, moveTime: TimeSpan.FromMilliseconds(50), threads: 1),
             games);

        Play("engine 50 ms", () => new SearchAgent(maxDepth: 40, moveTime: TimeSpan.FromMilliseconds(50), threads: 1),
             "heuristic", () => new HeuristicAgent(BotStrength.Normal, seed: 7),
             games);

        Console.WriteLine();
    }

    private static void Play(
        string nameA,
        Func<IQuoridorAgent> makeA,
        string nameB,
        Func<IQuoridorAgent> makeB,
        int games) =>
        Play(nameA, makeA, nameB, makeB, games, (random, _) => RandomOpening(random, plies: 4));

    private static void Play(
        string nameA,
        Func<IQuoridorAgent> makeA,
        string nameB,
        Func<IQuoridorAgent> makeB,
        int games,
        Func<Random, int, GameState> opening) =>
        Play(nameA, makeA, nameB, makeB, games, opening, ponderMs: 0);

    /// <summary>
    /// <paramref name="ponderMs"/> stands in for a human being slow. Before every move,
    /// whichever agent is waiting is given that long on the position its opponent is
    /// about to move from — which is the position the desktop hands a real ponder. Only
    /// an agent that was built able to ponder takes it, so one side of a duel can have
    /// the feature and the other not.
    /// </summary>
    private static void Play(
        string nameA,
        Func<IQuoridorAgent> makeA,
        string nameB,
        Func<IQuoridorAgent> makeB,
        int games,
        Func<Random, int, GameState> opening,
        int ponderMs)
    {
        int winsA = 0;
        int winsB = 0;
        int unfinished = 0;

        for (int game = 0; game < games; game++)
        {
            // Alternate colours, and start from a short random opening so the engines
            // are not replaying one deterministic game over and over.
            int playerA = game % 2;

            IQuoridorAgent[] agents = new IQuoridorAgent[2];
            agents[playerA] = makeA();
            agents[playerA ^ 1] = makeB();

            GameState state = opening(new Random(1000 + game), game);

            var history = new List<ulong>();

            int ply = 0;
            for (; ply < 300 && !state.IsGameOver; ply++)
            {
                // The waiting agent thinks first, on the position its opponent is looking
                // at, exactly as the desktop does while a human decides. Synchronous, so
                // the two searches never overlap — which is also true in the app, where
                // the ponder is stopped and waited for before the real one starts.
                if (ponderMs > 0
                    && agents[state.SideToMove ^ 1] is SearchAgent waiting && waiting.CanPonder)
                {
                    using var stop = new CancellationTokenSource(ponderMs);
                    waiting.Ponder(state, CollectionsMarshal.AsSpan(history), stop.Token);
                }

                IQuoridorAgent agent = agents[state.SideToMove];
                agent.SetGameHistory(CollectionsMarshal.AsSpan(history));

                Move move = agent.ChooseMove(state);

                if (!state.IsLegal(move))
                    throw new InvalidOperationException($"{(agent == agents[playerA] ? nameA : nameB)} played illegal {move}");

                history.Add(state.Hash);
                state.Apply(move);
            }

            if (state.IsGameOver)
            {
                if (state.Winner == playerA) winsA++;
                else winsB++;
            }
            else
            {
                unfinished++;
            }
        }

        string tail = unfinished > 0 ? $", {unfinished} unfinished" : string.Empty;
        Console.WriteLine($"  {nameA} {winsA} : {winsB} {nameB}{tail}");
    }

    // ==================================================================== smp ==

    private static void ThreadScaling()
    {
        Console.WriteLine("Thread scaling, 1000 ms per search");
        Console.WriteLine("  position          threads   depth      nodes");

        int maxThreads = Math.Clamp(Environment.ProcessorCount, 1, 8);

        foreach ((string name, GameState state) in Positions())
        {
            foreach (int threads in new[] { 1, 2, maxThreads })
            {
                if (threads > maxThreads) continue;

                var agent = new SearchAgent(maxDepth: 40, moveTime: TimeSpan.FromMilliseconds(1000), threads: threads);
                agent.ChooseMove(state);

                SearchResult result = agent.LastResult;
                Console.WriteLine($"  {name,-16} {threads,7} {result.Depth,7} {result.Nodes,10:N0}");
            }
        }

        Console.WriteLine();
    }

    // ============================================================== positions ==

    private static IEnumerable<(string Name, GameState State)> Positions()
    {
        yield return ("opening", GameState.CreateInitial());

        GameState middle = GameState.CreateInitial();
        foreach (string text in new[] { "e2", "e8", "e3", "e7", "e6h", "d7", "e4", "d6", "d4v", "c6" })
        {
            if (!Notation.TryParse(text, out Move move) || !middle.IsLegal(move))
                throw new InvalidOperationException($"bad benchmark line at {text}");

            middle.Apply(move);
        }

        yield return ("middlegame", middle);

        GameState race = GameState.Create(
            pawn0: Board.Index(5, 4), pawn1: Board.Index(3, 3),
            walls0: 0, walls1: 1, sideToMove: 0);

        yield return ("endgame", race);

        // A board with squares out of play, opened the same way. This is the position
        // the wall-graph shortcut used to give up on entirely.
        GameState holed = new GameSetup { Holes = 6, Seed = 11 }.Build().State;
        foreach (string text in new[] { "e2", "e8", "e3", "e7" })
        {
            if (Notation.TryParse(text, out Move move) && holed.IsLegal(move)) holed.Apply(move);
        }

        yield return ("holes", holed);

        // A board with portals, opened the same way. Its own row wherever this list is
        // printed, and never averaged with the others: the flood fill follows a fifth edge
        // out of every mouth, so this is the slowest board the engine ever searches, and
        // burying that in a mean would be hiding the price of the feature.
        GameState warped = new GameSetup { Portals = 2, Seed = 19 }.Build().State;
        foreach (string text in new[] { "e2", "e8", "e3", "e7" })
        {
            if (Notation.TryParse(text, out Move move) && warped.IsLegal(move)) warped.Apply(move);
        }

        yield return ("portals", warped);
    }

    private static GameState RandomOpening(Random random, int plies)
    {
        GameState state = GameState.CreateInitial();

        for (int i = 0; i < plies; i++)
        {
            var moves = state.LegalMoves();
            state.Apply(moves[random.Next(moves.Count)]);
        }

        return state;
    }

    /// <summary>A board with squares out of play, opened a few random plies.</summary>
    private static GameState HoleOpening(Random random, int seed, int plies)
    {
        GameState state = new GameSetup { Holes = 8, Seed = seed }.Build().State;

        for (int i = 0; i < plies; i++)
        {
            var moves = state.LegalMoves();
            state.Apply(moves[random.Next(moves.Count)]);
        }

        return state;
    }

    /// <summary>A board with pickups on it, opened a few random plies like the others.</summary>
    private static GameState PickupOpening(Random random, int seed, int plies)
    {
        GameState state = new GameSetup { Pickups = 8, Seed = seed }.Build().State;

        for (int i = 0; i < plies; i++)
        {
            var moves = state.LegalMoves();
            state.Apply(moves[random.Next(moves.Count)]);
        }

        return state;
    }

    /// <summary>Two portals and nothing else, so the measurement is of the edges alone.</summary>
    private static GameState PortalOpening(Random random, int seed, int plies)
    {
        GameState state = new GameSetup { Portals = 2, Seed = seed }.Build().State;

        for (int i = 0; i < plies; i++)
        {
            var moves = state.LegalMoves();
            state.Apply(moves[random.Next(moves.Count)]);
        }

        return state;
    }
}
