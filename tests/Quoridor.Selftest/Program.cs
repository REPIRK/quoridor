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
        Run("alternative boards, holes and pickups", BlockedSquares);
        Run("a rolled game reaches every board and is always playable", RolledBoards);
        Run("pickups do what they say", PickupEffects);
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

        // A smaller game names its squares from its own corner, not the grid's, so a
        // 7x7 board runs a1 to g7 — which is what its margin prints too.
        GameState small = GameState.CreateInitial(7, 7);
        int origin = small.GoalRow(0);

        Check(Notation.Format(Move.Pawn(small.GoalRow(1), 4), small) == "d1",
            "the 7x7 board's near centre is d1");
        Check(Notation.Format(Move.Pawn(origin, origin), small) == "a7",
            "the 7x7 board's far corner is a7");

        foreach (string text in new[] { "a1", "d4", "g7", "a1h", "f6v" })
        {
            Check(Notation.TryParse(text, out Move parsed, origin), $"parses {text} on a 7x7 board");
            Check(Notation.Format(parsed, small) == text, $"{text} survives a round trip on a 7x7 board");
        }

        // The squares a 7x7 board names must be squares it actually has.
        Check(Notation.TryParse("a1", out Move corner, origin) &&
              Board.RowOf(corner.Cell) == small.GoalRow(1) && Board.ColOf(corner.Cell) == origin,
            "a1 on a 7x7 board is its own bottom-left, not the grid's");
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
        GameSetup[] setups =
        {
            GameSetup.Standard,
            new() { Holes = 6, Seed = 11 },
            new() { Holes = 10, Seed = 12 },
            new() { Size = 7, Walls = 7, Seed = 13 },
            new() { Size = 7, Walls = 6, Holes = 4, Seed = 14 },
            new() { Pickups = 6, Seed = 15 },
            new() { Size = 7, Walls = 6, Holes = 4, Pickups = 4, Seed = 16 },

            // The smallest board, which a rolled game can now land on. Everything is
            // tighter here: two rows of five are already the goal rows, so a hole or a
            // pickup has fifteen squares to go on and a wall has a quarter of the slots.
            new() { Size = 5, Walls = 2, Seed = 17 },
            new() { Size = 5, Walls = 3, Holes = 2, Pickups = 4, Seed = 18 },
        };

        foreach (GameSetup setup in setups)
        {
            string name = setup.Describe();
            BuiltBoard built = setup.Build();
            GameState start = built.State;

            Check(PathFinder.HasPath(start, 0) && PathFinder.HasPath(start, 1),
                $"{name}: both players start with a route");

            Check(start.WallsOf(0) == setup.Walls && start.WallsOf(1) == setup.Walls,
                $"{name}: both players start with the wall supply asked for");

            // A half turn of the grid maps one player's half onto the other's, so a fair
            // board is one that is unchanged by it.
            Check(Mirrored(built.Holes), $"{name}: the holes are symmetric");
            Check(Mirrored(start.WallPickups) && Mirrored(start.SkipPickups),
                $"{name}: the pickups are symmetric");

            Check((built.Holes & (Board.Bit(start.PawnOf(0)) | Board.Bit(start.PawnOf(1)))) == 0,
                $"{name}: no hole under a starting pawn");
            Check((built.Holes & (start.GoalMask(0) | start.GoalMask(1))) == 0,
                $"{name}: no hole in either goal row");

            for (int cell = 0; cell < Board.CellCount; cell++)
            {
                if ((built.Holes & Board.Bit(cell)) == 0) continue;

                for (int direction = 0; direction < 4; direction++)
                {
                    Check(start.Blocked(cell, direction), $"{name}: nothing leaves a hole");

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

                    Check(start.Blocked(neighbour, back), $"{name}: nothing steps into a hole");
                }
            }

            // A smaller game is played on a centred square, and the ring around it must
            // be out of reach — including for walls, which would otherwise be wasted on
            // squares nobody can stand on.
            if (setup.Size < Board.Size)
            {
                int origin = start.GoalRow(0);

                Check(origin == (Board.Size - setup.Size) / 2, $"{name}: the game is centred");
                Check(!start.IsSlotFree(MoveKind.HorizontalWall, origin - 1, origin),
                    $"{name}: no wall slots outside the game");
                Check(start.IsSlotFree(MoveKind.HorizontalWall, origin, origin),
                    $"{name}: wall slots inside the game are usable");
            }

            // Play the board out, auditing every geometrically legal wall as we go.
            var agents = new IQuoridorAgent[]
            {
                new HeuristicAgent(BotStrength.Normal, seed: 4),
                new SearchAgent(maxDepth: 4, moveTime: TimeSpan.FromMilliseconds(40), threads: 1, tableMegabytes: 4),
            };

            GameState state = start;
            bool sawPickup = false;
            int skipped = 0, checkedFully = 0;

            for (int ply = 0; ply < 400 && !state.IsGameOver; ply++)
            {
                foreach (MoveKind kind in new[] { MoveKind.HorizontalWall, MoveKind.VerticalWall })
                {
                    for (int row = 0; row < Board.SlotSize; row++)
                    {
                        for (int col = 0; col < Board.SlotSize; col++)
                        {
                            if (!state.IsSlotFree(kind, row, col)) continue;

                            if (WallGraph.CanDisconnect(state, kind, row, col))
                            {
                                checkedFully++;
                                continue;
                            }

                            skipped++;

                            GameState probe = state;
                            probe.PlaceWallUnchecked(kind, row, col);

                            if (PathFinder.HasPath(probe, 0) && PathFinder.HasPath(probe, 1)) continue;

                            Check(false, $"{name}: fast path cleared a sealing wall {new Move(kind, row, col)}");
                            return;
                        }
                    }
                }

                int mover = state.SideToMove;
                Move move = agents[mover].ChooseMove(state);

                if (!state.IsLegal(move))
                {
                    Check(false, $"{name} ply {ply}: illegal move {move}");
                    return;
                }

                int wallsBefore = state.WallsOf(mover);
                bool onSkip = move.Kind == MoveKind.Pawn &&
                              (state.SkipPickups & Board.Bit(move.Cell)) != 0;
                bool onWall = move.Kind == MoveKind.Pawn &&
                              (state.WallPickups & Board.Bit(move.Cell)) != 0;

                state.Apply(move);

                if (onWall)
                {
                    sawPickup = true;

                    int expected = Math.Min(wallsBefore + GameState.WallsPerPickup, Board.MaxWalls);
                    Check(state.WallsOf(mover) == expected, $"{name}: a wall pickup adds its walls");
                    Check(state.SideToMove != mover, $"{name}: a wall pickup still passes the turn");
                }

                if (onSkip && !state.IsGameOver)
                {
                    sawPickup = true;
                    Check(state.SideToMove == mover, $"{name}: a free move keeps the turn");
                }
            }

            Check(state.IsGameOver, $"{name}: the game reached a result");

            // The shortcut used to be switched off entirely once a board had holes. It
            // is not any more, and this is what it now saves — reported so a change that
            // quietly turned it off again would show up here rather than in a lost game.
            int total = skipped + checkedFully;
            if (total > 0)
            {
                Report($"{name}: {100 * skipped / total}% of wall checks skipped");

                if (setup.Holes > 0)
                    Check(skipped > 0, $"{name}: the wall-graph shortcut still works with holes");
            }

            // Not asserted: that a pickup was taken. Both bots walk their route and a
            // pickup a step off it is usually not worth the two tempi — which is correct
            // play, not a broken mechanic. The mechanic itself is checked below, where it
            // can be made to happen rather than waited for.
            _ = sawPickup;
        }
    }

    /// <summary>
    /// The rolled game. Every board the menus offer has to be reachable by the roll —
    /// 5×5 was offered under Custom for a while before the roll could produce it, and
    /// nothing failed, it simply never came up.
    ///
    /// And the numbers rolled alongside the size have to suit it. A five has a quarter
    /// of the wall slots a nine has and its two back rows are half its squares, so the
    /// generous end of the nine's range would build boards that fall back to a plain one
    /// — which is the same silence as before, one layer down. Both are checked here.
    /// </summary>
    private static void RolledBoards()
    {
        var seen = new Dictionary<int, int>();

        for (int seed = 0; seed < 600; seed++)
        {
            GameSetup setup = GameSetup.Roll(seed);
            string name = $"seed {seed} ({setup.Describe()})";

            Check(setup.Size is 5 or 7 or Board.Size, $"{name}: a size the board supports");
            seen[setup.Size] = seen.GetValueOrDefault(setup.Size) + 1;

            BuiltBoard built = setup.Build();
            GameState start = built.State;

            Check(PathFinder.HasPath(start, 0) && PathFinder.HasPath(start, 1),
                $"{name}: both players start with a route");

            Check(start.WallsOf(0) == setup.Walls && start.WallsOf(1) == setup.Walls,
                $"{name}: the wall supply survived the build");

            // The build gives up and returns a plain board when the numbers themselves
            // cannot be placed. That is a safety net, not an outcome a roll may reach:
            // asking for holes and getting none means the roll was beyond the board.
            Check(setup.Holes == 0 || built.Holes != 0,
                $"{name}: the holes asked for actually fit");

            Check(setup.Pickups == 0 || (start.WallPickups | start.SkipPickups) != 0,
                $"{name}: the pickups asked for actually fit");
        }

        foreach (int size in new[] { 5, 7, Board.Size })
        {
            Check(seen.GetValueOrDefault(size) > 0, $"{size}×{size} comes up in a rolled game");
            Report($"{size}×{size}: {seen.GetValueOrDefault(size)} of 600 rolls");
        }
    }

    /// <summary>
    /// The two pickups, driven directly rather than waited for. A spare wall adds to the
    /// supply and the turn passes as usual; a free move keeps the turn — except on the
    /// goal row, where another move would have nowhere to go.
    /// </summary>
    private static void PickupEffects()
    {
        GameState wall = GameState.Create(
            pawn0: Board.Index(4, 4), pawn1: Board.Index(0, 0),
            walls0: 3, walls1: 10, sideToMove: 0);

        wall.PlacePickup(Board.Index(3, 4), PickupKind.Wall);
        ulong before = wall.Hash;

        wall.Apply(Move.Pawn(3, 4));

        Check(wall.WallsOf(0) == 3 + GameState.WallsPerPickup, "the spare walls join the supply");
        Check(wall.SideToMove == 1, "and the turn passes as usual");
        Check(wall.WallPickups == 0, "the square is empty afterwards");
        Check(wall.Hash != before, "the hash moved with it");
        Check(!wall.HasPickups, "the board reports nothing left to pick up");

        GameState skip = GameState.Create(
            pawn0: Board.Index(4, 4), pawn1: Board.Index(0, 0),
            walls0: 10, walls1: 10, sideToMove: 0);

        skip.PlacePickup(Board.Index(3, 4), PickupKind.Skip);
        skip.Apply(Move.Pawn(3, 4));

        Check(skip.SideToMove == 0, "a free move keeps the turn");
        Check(skip.WallsOf(0) == 10, "and hands out nothing else");
        Check(skip.Ply == 1, "the move still counts as a ply");

        // Two positions that differ only in an uncollected pickup must not share a hash,
        // or the transposition table would answer one with the other.
        GameState bare = GameState.Create(
            pawn0: Board.Index(4, 4), pawn1: Board.Index(0, 0),
            walls0: 10, walls1: 10, sideToMove: 0);

        GameState loaded = bare;
        loaded.PlacePickup(Board.Index(2, 2), PickupKind.Skip);

        Check(bare.Hash != loaded.Hash, "a pickup on the board changes the hash");

        // Stepping onto the goal row over a free move: the game is over, so the extra
        // move is not handed out and the turn passes like any other winning move. That
        // keeps the invariant the search relies on — a finished game is never left with
        // the winner on move.
        GameState finish = GameState.Create(
            pawn0: Board.Index(1, 4), pawn1: Board.Index(8, 0),
            walls0: 0, walls1: 0, sideToMove: 0);

        finish.PlacePickup(Board.Index(0, 4), PickupKind.Skip);
        finish.Apply(Move.Pawn(0, 4));

        Check(finish.Winner == 0, "reaching the goal row still wins");
        Check(finish.SideToMove == 1, "and a won game never leaves the winner on move");
    }

    /// <summary>Whether a set of squares is unchanged by a half turn of the grid.</summary>
    private static bool Mirrored(UInt128 cells)
    {
        for (int cell = 0; cell < Board.CellCount; cell++)
        {
            bool here = (cells & Board.Bit(cell)) != 0;
            bool opposite = (cells & Board.Bit(Board.CellCount - 1 - cell)) != 0;

            if (here != opposite) return false;
        }

        return true;
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

    /// <summary>A measurement worth printing next to the checks, but not a check itself.</summary>
    private static void Report(string what) => Console.WriteLine($"      {what}");
}
