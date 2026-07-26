using System.Diagnostics;
using Quoridor.Core;
using Quoridor.Engine;

namespace Quoridor.Selftest;

/// <summary>
/// Rule verification without a test framework dependency, so it runs on a clean
/// machine with nothing but the .NET SDK. Exits non-zero when anything fails.
/// </summary>
internal static class Program
{
    private static int _failures;

    private static int Main()
    {
        Run("initial position", InitialPosition);
        Run("wall geometry", WallGeometry);
        Run("wall blocks movement", WallBlocksMovement);
        Run("wall may not seal a player in", WallMayNotSeal);
        Run("straight jump over opponent", StraightJump);
        Run("side steps when the jump is blocked", SideStepJump);
        Run("notation round trip", NotationRoundTrip);
        Run("win detection", WinDetection);
        Run("hash is order independent", HashConsistency);
        Run("bot games terminate legally", BotPlayouts);
        Run("boards with squares out of play", BlockedSquares);
        Run("wall-graph fast path never hides a block", WallGraphFastPath);
        Run("progress test never hides a change in distance", ProgressShortcut);
        Run("search engine respects its budget and outplays the heuristic", SearchAgentStrength);
        Run("path finding throughput", PathThroughput);

        Console.WriteLine();
        if (_failures == 0)
        {
            Console.WriteLine("All checks passed.");
            return 0;
        }

        Console.WriteLine($"{_failures} check(s) FAILED.");
        return 1;
    }

    // ------------------------------------------------------------------ tests --

    private static void InitialPosition()
    {
        GameState state = GameState.CreateInitial();

        Check(state.PawnOf(0) == Board.Index(8, 4), "player 1 starts on e1");
        Check(state.PawnOf(1) == Board.Index(0, 4), "player 2 starts on e9");
        Check(state.WallsOf(0) == 10 && state.WallsOf(1) == 10, "ten walls each");
        Check(state.SideToMove == 0, "player 1 moves first");
        Check(PathFinder.Distance(state, 0) == 8, "player 1 is eight steps from goal");
        Check(PathFinder.Distance(state, 1) == 8, "player 2 is eight steps from goal");

        Span<Move> buffer = stackalloc Move[8];
        Check(state.GeneratePawnMoves(buffer) == 3, "three steps from the starting edge cell");
    }

    private static void WallGeometry()
    {
        GameState state = GameState.CreateInitial();
        state.Apply(new Move(MoveKind.HorizontalWall, 4, 4));

        Check(!state.IsSlotFree(MoveKind.HorizontalWall, 4, 4), "a slot holds one wall");
        Check(!state.IsSlotFree(MoveKind.VerticalWall, 4, 4), "walls may not cross");
        Check(!state.IsSlotFree(MoveKind.HorizontalWall, 4, 3), "no overlap with the left neighbour");
        Check(!state.IsSlotFree(MoveKind.HorizontalWall, 4, 5), "no overlap with the right neighbour");
        Check(state.IsSlotFree(MoveKind.HorizontalWall, 4, 6), "two slots away is fine");
        Check(state.IsSlotFree(MoveKind.VerticalWall, 4, 3), "a perpendicular neighbour is fine");
        Check(state.IsSlotFree(MoveKind.VerticalWall, 5, 4), "a vertical wall below is fine");
        Check(state.WallsOf(0) == 9, "the wall came out of player 1's supply");
        Check(state.SideToMove == 1, "turn passed after the wall");
    }

    private static void WallBlocksMovement()
    {
        GameState state = GameState.CreateInitial();
        state.PlaceWallUnchecked(MoveKind.HorizontalWall, 4, 4);

        Check(state.Blocked(Board.Index(4, 4), Board.South), "blocks south of the upper-left cell");
        Check(state.Blocked(Board.Index(4, 5), Board.South), "blocks south of the upper-right cell");
        Check(state.Blocked(Board.Index(5, 4), Board.North), "blocks north of the lower-left cell");
        Check(state.Blocked(Board.Index(5, 5), Board.North), "blocks north of the lower-right cell");
        Check(!state.Blocked(Board.Index(4, 6), Board.South), "leaves the next column alone");

        GameState vertical = GameState.CreateInitial();
        vertical.PlaceWallUnchecked(MoveKind.VerticalWall, 4, 4);

        Check(vertical.Blocked(Board.Index(4, 4), Board.East), "blocks east of the upper cell");
        Check(vertical.Blocked(Board.Index(5, 4), Board.East), "blocks east of the lower cell");
        Check(vertical.Blocked(Board.Index(4, 5), Board.West), "blocks west of the upper-right cell");
        Check(!vertical.Blocked(Board.Index(6, 4), Board.East), "leaves the next row alone");
    }

    private static void WallMayNotSeal()
    {
        // Player 1 tucked into the bottom-left corner: one wall above them is fine,
        // but adding the wall that closes the corner would leave no route at all.
        GameState state = GameState.Create(
            pawn0: Board.Index(8, 0), pawn1: Board.Index(0, 4),
            walls0: 10, walls1: 10, sideToMove: 1);

        Check(state.IsWallLegal(MoveKind.HorizontalWall, 7, 0), "capping the corner from above is legal");
        state.PlaceWallUnchecked(MoveKind.HorizontalWall, 7, 0);

        Check(PathFinder.HasPath(state, 0), "player 1 can still get out sideways");
        Check(!state.IsWallLegal(MoveKind.VerticalWall, 7, 1), "closing the last exit is rejected");

        state.PlaceWallUnchecked(MoveKind.VerticalWall, 7, 1);
        Check(!PathFinder.HasPath(state, 0), "and it really does seal them in");
        Check(PathFinder.Distance(state, 0) == -1, "distance reports unreachable");
    }

    private static void StraightJump()
    {
        GameState state = GameState.Create(
            pawn0: Board.Index(4, 4), pawn1: Board.Index(3, 4),
            walls0: 10, walls1: 10, sideToMove: 0);

        var moves = Collect(state);

        Check(moves.Contains(Move.Pawn(2, 4)), "hops straight over the opponent");
        Check(!moves.Contains(Move.Pawn(3, 4)), "may not land on the opponent");
        Check(moves.Contains(Move.Pawn(5, 4)), "may still retreat");
        Check(moves.Count == 4, "four options: jump, retreat, two sideways");
    }

    private static void SideStepJump()
    {
        GameState state = GameState.Create(
            pawn0: Board.Index(4, 4), pawn1: Board.Index(3, 4),
            walls0: 10, walls1: 10, sideToMove: 0);

        // A wall directly behind the opponent turns the jump into a diagonal choice.
        state.PlaceWallUnchecked(MoveKind.HorizontalWall, 2, 4);

        var moves = Collect(state);

        Check(!moves.Contains(Move.Pawn(2, 4)), "the straight jump is closed");
        Check(moves.Contains(Move.Pawn(3, 3)), "diagonal to the left is open");
        Check(moves.Contains(Move.Pawn(3, 5)), "diagonal to the right is open");

        // Now fence the left diagonal off too and only the right one should remain.
        GameState narrowed = state;
        narrowed.PlaceWallUnchecked(MoveKind.VerticalWall, 3, 3);

        var narrowedMoves = Collect(narrowed);
        Check(!narrowedMoves.Contains(Move.Pawn(3, 3)), "walled-off diagonal is excluded");
        Check(narrowedMoves.Contains(Move.Pawn(3, 5)), "the open diagonal remains");
    }

    private static void NotationRoundTrip()
    {
        Check(Notation.Format(Move.Pawn(8, 4)) == "e1", "e1 is the bottom centre");
        Check(Notation.Format(Move.Pawn(0, 0)) == "a9", "a9 is the top left");
        Check(Notation.Format(new Move(MoveKind.HorizontalWall, 4, 4)) == "e4h", "wall notation");

        foreach (string text in new[] { "e1", "a9", "i5", "e4h", "b7v" })
        {
            Check(Notation.TryParse(text, out Move parsed), $"parses {text}");
            Check(Notation.Format(parsed) == text, $"{text} survives a round trip");
        }

        Check(!Notation.TryParse("z9", out _), "rejects an out-of-range file");
        Check(!Notation.TryParse("e4x", out _), "rejects an unknown orientation");
    }

    private static void WinDetection()
    {
        GameState state = GameState.Create(
            pawn0: Board.Index(1, 4), pawn1: Board.Index(7, 4),
            walls0: 3, walls1: 3, sideToMove: 0);

        Check(state.Winner == -1, "no winner yet");

        state.Apply(Move.Pawn(0, 4));
        Check(state.Winner == 0, "player 1 wins on reaching the top row");
        Check(state.IsGameOver, "game is over");
    }

    private static void HashConsistency()
    {
        GameState a = GameState.CreateInitial();
        a.Apply(Move.Pawn(7, 4));
        a.Apply(new Move(MoveKind.HorizontalWall, 2, 2));
        a.Apply(new Move(MoveKind.VerticalWall, 5, 6));

        GameState b = GameState.CreateInitial();
        b.Apply(new Move(MoveKind.HorizontalWall, 2, 2));  // player 1's wall...
        b.Apply(Move.Pawn(1, 4));
        b.Apply(Move.Pawn(7, 4));

        Check(a.Hash != b.Hash, "different positions hash differently");

        GameState c = GameState.CreateInitial();
        c.Apply(Move.Pawn(7, 4));
        c.Apply(new Move(MoveKind.HorizontalWall, 2, 2));
        c.Apply(new Move(MoveKind.VerticalWall, 5, 6));

        Check(a.Hash == c.Hash, "the same move order hashes the same");
    }

    private static void BotPlayouts()
    {
        const int games = 12;
        int finished = 0;

        for (int game = 0; game < games; game++)
        {
            GameState state = GameState.CreateInitial();

            IQuoridorAgent[] agents =
            {
                new HeuristicAgent(BotStrength.Normal, seed: game),
                new HeuristicAgent(BotStrength.Easy, seed: 1000 + game),
            };

            for (int ply = 0; ply < 400 && !state.IsGameOver; ply++)
            {
                Move move = agents[state.SideToMove].ChooseMove(state);

                if (!state.IsLegal(move))
                {
                    Check(false, $"game {game} ply {ply}: bot returned illegal move {move}");
                    return;
                }

                state.Apply(move);
            }

            if (state.IsGameOver) finished++;

            Check(PathFinder.HasPath(state, 0) || state.Winner == 0, "player 1 never got sealed off");
            Check(PathFinder.HasPath(state, 1) || state.Winner == 1, "player 2 never got sealed off");
        }

        Check(finished == games, $"all {games} bot games reached a result ({finished} did)");
    }

    /// <summary>
    /// The alternative boards. Three things have to hold, and the third is the one that
    /// would be quiet if it broke: a hole must be sealed from both directions, both
    /// players must still have a route from the start, and the engine's fast legality
    /// path must not clear a wall that seals someone in — which it could, because its
    /// reasoning knows about walls and borders but not about holes.
    /// </summary>
    private static void BlockedSquares()
    {
        foreach (BoardLayout layout in Layouts.All)
        {
            GameState start = GameState.CreateInitial(layout);
            UInt128 holes = Layouts.Holes(layout);

            Check(start.HasHoles == (holes != 0), $"{Layouts.Name(layout)}: the position knows whether it has holes");
            Check(PathFinder.HasPath(start, 0) && PathFinder.HasPath(start, 1),
                $"{Layouts.Name(layout)}: both players start with a route");

            for (int cell = 0; cell < Board.CellCount; cell++)
            {
                if ((holes & Board.Bit(cell)) == 0) continue;

                Check(cell != start.PawnOf(0) && cell != start.PawnOf(1),
                    $"{Layouts.Name(layout)}: no hole under a starting pawn");

                for (int direction = 0; direction < 4; direction++)
                {
                    Check(start.Blocked(cell, direction), $"{Layouts.Name(layout)}: nothing leaves a hole");

                    int neighbour = cell + Board.Delta[direction];
                    if (neighbour < 0 || neighbour >= Board.CellCount) continue;

                    // Wrapping round a row would make this a different neighbour entirely.
                    if ((direction == Board.West || direction == Board.East) &&
                        Board.RowOf(neighbour) != Board.RowOf(cell))
                    {
                        continue;
                    }

                    int back = direction switch
                    {
                        Board.North => Board.South,
                        Board.South => Board.North,
                        Board.West => Board.East,
                        _ => Board.West,
                    };

                    Check(start.Blocked(neighbour, back), $"{Layouts.Name(layout)}: nothing steps into a hole");
                }
            }

            // Play the board out, auditing every geometrically legal wall as we go.
            var agents = new IQuoridorAgent[]
            {
                new HeuristicAgent(BotStrength.Normal, seed: 4),
                new SearchAgent(maxDepth: 4, moveTime: TimeSpan.FromMilliseconds(40), threads: 1, tableMegabytes: 4),
            };

            GameState state = start;

            for (int ply = 0; ply < 300 && !state.IsGameOver; ply++)
            {
                foreach (MoveKind kind in new[] { MoveKind.HorizontalWall, MoveKind.VerticalWall })
                {
                    for (int row = 0; row < Board.SlotSize; row++)
                    {
                        for (int col = 0; col < Board.SlotSize; col++)
                        {
                            if (!state.IsSlotFree(kind, row, col)) continue;
                            if (WallGraph.CanDisconnect(state, kind, row, col)) continue;

                            GameState probe = state;
                            probe.PlaceWallUnchecked(kind, row, col);

                            if (PathFinder.HasPath(probe, 0) && PathFinder.HasPath(probe, 1)) continue;

                            Check(false, $"{Layouts.Name(layout)}: fast path cleared a sealing wall {new Move(kind, row, col)}");
                            return;
                        }
                    }
                }

                Move move = agents[state.SideToMove].ChooseMove(state);

                if (!state.IsLegal(move))
                {
                    Check(false, $"{Layouts.Name(layout)} ply {ply}: illegal move {move}");
                    return;
                }

                state.Apply(move);
            }

            Check(state.IsGameOver, $"{Layouts.Name(layout)}: the game reached a result");
        }
    }

    /// <summary>
    /// The wall-graph test claims a placement provably cannot cut the board, and the
    /// search trusts that claim instead of running two flood fills. If it were ever
    /// wrong the engine would happily produce an illegal wall, so audit every
    /// geometrically legal placement across a spread of real positions.
    /// </summary>
    private static void WallGraphFastPath()
    {
        var random = new Random(20260725);
        MoveKind[] orientations = { MoveKind.HorizontalWall, MoveKind.VerticalWall };

        int skipped = 0;
        int stillChecked = 0;

        for (int game = 0; game < 30; game++)
        {
            GameState state = GameState.CreateInitial();

            for (int ply = 0; ply < 22 && !state.IsGameOver; ply++)
            {
                for (int row = 0; row < Board.SlotSize; row++)
                {
                    for (int col = 0; col < Board.SlotSize; col++)
                    {
                        foreach (MoveKind kind in orientations)
                        {
                            if (!state.IsSlotFree(kind, row, col)) continue;

                            if (WallGraph.CanDisconnect(state, kind, row, col))
                            {
                                stillChecked++;
                                continue;
                            }

                            skipped++;

                            GameState probe = state;
                            probe.PlaceWallUnchecked(kind, row, col);

                            if (!PathFinder.HasPath(probe, 0) || !PathFinder.HasPath(probe, 1))
                            {
                                Check(false, $"fast path cleared a sealing wall: {new Move(kind, row, col)}");
                                return;
                            }
                        }
                    }
                }

                var moves = state.LegalMoves();
                state.Apply(moves[random.Next(moves.Count)]);
            }
        }

        double saved = 100.0 * skipped / (skipped + stillChecked);
        Console.WriteLine($"      {skipped} placements skipped the path check, {stillChecked} still needed it ({saved:F0}% saved)");

        Check(skipped > 0, "the fast path actually fires");
        Check(saved > 40, "the fast path saves a meaningful share of the work");
    }

    /// <summary>
    /// The generator skips all path work for a wall when neither of the two edges it
    /// closes is a step in a player's distance map. That is an exactness claim the
    /// search leans on hard — a wall wrongly cleared is an illegal move — so check it
    /// against the truth for every geometrically legal placement across many positions.
    /// </summary>
    private static void ProgressShortcut()
    {
        var random = new Random(31415);
        MoveKind[] orientations = { MoveKind.HorizontalWall, MoveKind.VerticalWall };

        Span<byte> distances = stackalloc byte[Board.CellCount];
        int skipped = 0;
        int measured = 0;

        for (int game = 0; game < 30; game++)
        {
            GameState state = GameState.CreateInitial();

            for (int ply = 0; ply < 20 && !state.IsGameOver; ply++)
            {
                for (int player = 0; player < 2; player++)
                {
                    PathFinder.FillDistancesToGoal(state, player, distances);
                    int before = distances[state.PawnOf(player)];

                    for (int row = 0; row < Board.SlotSize; row++)
                    {
                        for (int col = 0; col < Board.SlotSize; col++)
                        {
                            foreach (MoveKind kind in orientations)
                            {
                                if (!state.IsSlotFree(kind, row, col)) continue;

                                if (MoveCandidates.ClosesProgress(kind, row, col, distances))
                                {
                                    measured++;
                                    continue;
                                }

                                skipped++;

                                GameState probe = state;
                                probe.PlaceWallUnchecked(kind, row, col);

                                int after = PathFinder.Distance(probe, player);
                                if (after == before) continue;

                                Check(false, $"shortcut cleared {new Move(kind, row, col)} but distance " +
                                             $"for player {player + 1} went {before} to {after}");
                                return;
                            }
                        }
                    }
                }

                var moves = state.LegalMoves();
                state.Apply(moves[random.Next(moves.Count)]);
            }
        }

        double saved = 100.0 * skipped / (skipped + measured);
        Console.WriteLine($"      {skipped} placements provably left the route alone, {measured} did not ({saved:F0}%)");

        Check(skipped > 0, "the shortcut actually fires");
    }

    /// <summary>
    /// The engine runs on a background thread with a deadline, so the two things that
    /// matter are that it never blows the deadline and that the depth actually buys
    /// strength over the one-ply agent.
    /// </summary>
    private static void SearchAgentStrength()
    {
        const int games = 6;
        int searchWins = 0;
        double worstMove = 0;
        long totalNodes = 0;
        int deepest = 0;

        for (int game = 0; game < games; game++)
        {
            GameState state = GameState.CreateInitial();

            // Single-threaded and modestly timed so the result is reproducible.
            var search = new SearchAgent(maxDepth: 32, moveTime: TimeSpan.FromMilliseconds(250), threads: 1);
            var heuristic = new HeuristicAgent(BotStrength.Normal, seed: 500 + game);

            int searchPlayer = game % 2;
            IQuoridorAgent[] agents = searchPlayer == 0
                ? new IQuoridorAgent[] { search, heuristic }
                : new IQuoridorAgent[] { heuristic, search };

            for (int ply = 0; ply < 400 && !state.IsGameOver; ply++)
            {
                var clock = Stopwatch.StartNew();
                Move move = agents[state.SideToMove].ChooseMove(state);
                clock.Stop();

                worstMove = Math.Max(worstMove, clock.Elapsed.TotalMilliseconds);

                if (!state.IsLegal(move))
                {
                    Check(false, $"game {game} ply {ply}: illegal move {move}");
                    return;
                }

                state.Apply(move);

                totalNodes += search.LastResult.Nodes;
                deepest = Math.Max(deepest, search.LastResult.Depth);
            }

            if (state.Winner == searchPlayer) searchWins++;
        }

        Console.WriteLine($"      engine won {searchWins}/{games}, slowest move {worstMove:F0} ms, " +
                          $"deepest {deepest} ply, {totalNodes / games:N0} nodes per game");

        Check(worstMove < 700, "no move overran the 250 ms budget by more than a safety margin");
        Check(searchWins >= games - 1, "the engine beats the one-ply agent nearly every game");
        Check(deepest >= 6, "iterative deepening reaches a serious depth inside the budget");
    }

    private static void PathThroughput()
    {
        GameState state = GameState.CreateInitial();
        state.PlaceWallUnchecked(MoveKind.HorizontalWall, 3, 1);
        state.PlaceWallUnchecked(MoveKind.VerticalWall, 5, 4);
        state.PlaceWallUnchecked(MoveKind.HorizontalWall, 6, 6);

        const int iterations = 200_000;
        var clock = Stopwatch.StartNew();
        long sink = 0;
        for (int i = 0; i < iterations; i++) sink += PathFinder.Distance(state, i & 1);
        clock.Stop();

        double nanosecondsPer = clock.Elapsed.TotalMilliseconds * 1_000_000 / iterations;
        Console.WriteLine($"      shortest path: {nanosecondsPer:F0} ns/call (checksum {sink})");
        Check(nanosecondsPer < 20_000, "path finding is fast enough for search");
    }

    // ---------------------------------------------------------------- harness --

    private static List<Move> Collect(in GameState state)
    {
        Span<Move> buffer = stackalloc Move[8];
        int count = state.GeneratePawnMoves(buffer);

        var moves = new List<Move>(count);
        for (int i = 0; i < count; i++) moves.Add(buffer[i]);
        return moves;
    }

    private static void Run(string name, Action test)
    {
        int before = _failures;
        Console.WriteLine($"  {name}");

        try
        {
            test();
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"      EXCEPTION {ex.GetType().Name}: {ex.Message}");
        }

        if (_failures == before) Console.WriteLine("      ok");
    }

    private static void Check(bool condition, string what)
    {
        if (condition) return;
        _failures++;
        Console.WriteLine($"      FAIL: {what}");
    }
}
