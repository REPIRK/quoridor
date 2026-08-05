using System.Diagnostics;
using Quoridor.Core;
using Quoridor.Engine;

namespace Quoridor.Bench;

/// <summary>
/// Measures the engine rather than testing it. Three questions:
/// how deep does it get, does thinking longer actually make it play better, and does
/// adding threads buy anything.
///
///   dotnet run -c Release --project tests/Quoridor.Bench -- [depth|ladder|smp|portals|all]
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "all";

        Console.WriteLine($"{Environment.ProcessorCount} logical cores, {(Environment.Is64BitProcess ? "x64" : "x86")}");
        Console.WriteLine();

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

        return 0;
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
        Func<Random, int, GameState> opening)
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
                IQuoridorAgent agent = agents[state.SideToMove];
                agent.SetGameHistory(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(history));

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
