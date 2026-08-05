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
        Run("every board the setup offers carries the numbers it names", BoardCapacity);
        Run("the built board has not moved since it was recorded", FrozenBoards);
        Run("pickups do what they say", PickupEffects);
        Run("a portal step is an ordinary move that passes the turn", PortalMoves);
        Run("a route through a portal is the length it looks", PortalRoutes);
        Run("portals are permanent, and the hash is built on it", PortalEffects);
        Run("wall-graph fast path never hides a block", WallGraphFastPath);
        Run("progress test never hides a change in distance", ProgressShortcut);
        Run("the wall-less race verdict matches exact play", RaceVerdict);
        Run("what a portal board would ask of the race verdict", PortalRaceVerdict);
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

        Span<Move> buffer = stackalloc Move[10];
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

            // Portal boards. A mouth is an ordinary square with a fifth neighbour, so
            // every claim above has to go on holding on one — and the wall audit in the
            // playout below is the one worth having here, because the proof it audits is
            // stated over a planar graph of walls and holes that a portal is not part of.
            new() { Portals = 2, Seed = 19 },
            new() { Holes = 6, Portals = 2, Seed = 20 },
            new() { Pickups = 6, Portals = 1, Seed = 21 },
            new() { Size = 7, Walls = 6, Holes = 4, Portals = 1, Seed = 22 },

            // A five carries no portal however many are asked for: its two goal rows and
            // the rows beside them are the whole board, and a mouth may sit on none of
            // them. That is the placement rule speaking, not a special case for the size.
            new() { Size = 5, Walls = 3, Portals = 2, Seed = 23 },
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

            CheckPortalPlacement(setup, built, name);

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

                            // Portals only ever add edges, so a supergraph of a connected
                            // graph is connected and a wall that leaves both players a
                            // route on the plain board has to leave them one here too.
                            // That monotonicity is the whole argument for why the
                            // wall-graph shortcut needed no portal term, so it is checked
                            // rather than quoted.
                            if (state.HasPortals && !PortalsOnlyHelp(state, kind, row, col))
                            {
                                Check(false, $"{name}: {new Move(kind, row, col)} keeps both routes open " +
                                             "without the portals and closes one with them");
                                return;
                            }

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

                CheckPawnMoves(state, $"{name} ply {ply}");

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

    /// <summary>The portal pairs on a board, as the two squares each one links.</summary>
    private static List<(int A, int B)> PortalPairs(in GameState state)
    {
        var pairs = new List<(int, int)>();
        ulong bits = state.Portals;

        while (bits != 0)
        {
            int low = System.Numerics.BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;
            pairs.Add((low, Board.CellCount - 1 - low));
        }

        return pairs;
    }

    /// <summary>
    /// Where a portal is allowed to be, against where the build actually put it. Every
    /// clause is load-bearing somewhere else. The half turn is what makes a portal board
    /// fair by its geometry instead of by the placement having been careful about it. The
    /// row rules are what stop a portal from being a free ride down the board — a nine's
    /// portal from row 1 to row 7 would save six rows of a seven-row journey in every game
    /// ever played on that board — and what stop it from being a sideways shuffle within
    /// the centre row. And the four-square separation is what keeps the far mouth's escape
    /// squares out of the four ordinary directions: mouths two apart share a neighbour, and
    /// then a pawn on one with the opponent on the other is offered that shared square
    /// twice — a subtree searched twice at every node, and one entry closer to the end of
    /// a buffer whose bound was proved on the assumption it cannot happen.
    /// </summary>
    private static void CheckPortalPlacement(GameSetup setup, BuiltBoard built, string name)
    {
        GameState state = built.State;
        List<(int A, int B)> portals = PortalPairs(state);

        Check(portals.Count == setup.ActualPortals,
            $"{name}: {setup.ActualPortals} portals asked for and {portals.Count} placed");

        Check(state.HasPortals == (portals.Count > 0), $"{name}: the board knows whether it has portals");

        if (setup.Size == 5)
            Check(portals.Count == 0, $"{name}: a five carries no portal however many are asked for");

        if (portals.Count == 0) return;

        Check(Board.PopCount(state.PortalMouths()) == 2 * portals.Count,
            $"{name}: no square carries two mouths");

        int goal0 = state.GoalRow(0), goal1 = state.GoalRow(1);
        int centre = (goal0 + goal1) / 2;

        UInt128 pawns = Board.Bit(state.PawnOf(0)) | Board.Bit(state.PawnOf(1));
        UInt128 pickups = state.WallPickups | state.SkipPickups;

        Span<byte> toGoal0 = stackalloc byte[Board.CellCount];
        Span<byte> toGoal1 = stackalloc byte[Board.CellCount];
        PathFinder.FillDistancesToGoal(state, 0, toGoal0);
        PathFinder.FillDistancesToGoal(state, 1, toGoal1);

        foreach ((int a, int b) in portals)
        {
            string pair = $"{Notation.Format(Move.ToCell(a), state)}–{Notation.Format(Move.ToCell(b), state)}";

            // Asserted per portal rather than through a symmetric-mask test: a mask can be
            // symmetric while the pairing inside it is wrong, and the pairing is what the
            // move generator and the flood fill both read.
            Check(b == Board.CellCount - 1 - a, $"{name}: {pair} are each other's half-turn image");

            Check(Math.Abs(Board.RowOf(a) - Board.RowOf(b)) +
                  Math.Abs(Board.ColOf(a) - Board.ColOf(b)) >= 4,
                $"{name}: {pair} stand four squares apart, so the mouths share no neighbour");

            foreach (int mouth in new[] { a, b })
            {
                int row = Board.RowOf(mouth);

                Check(row > goal0 + 1 && row < goal1 - 1,
                    $"{name}: {pair} avoids the goal rows and the rows beside them");
                Check(row != centre, $"{name}: {pair} avoids the centre row");

                Check((pawns & Board.Bit(mouth)) == 0, $"{name}: {pair} is clear of both starting pawns");
                Check((built.Holes & Board.Bit(mouth)) == 0, $"{name}: {pair} is clear of the holes");
                Check((pickups & Board.Bit(mouth)) == 0, $"{name}: {pair} is clear of the pickups");

                Check(toGoal0[mouth] != PathFinder.Unreachable && toGoal1[mouth] != PathFinder.Unreachable,
                    $"{name}: both players can reach {pair}");
            }
        }

        // Two pairs whose mouths sit in neighbouring files are one objective in two
        // colours, and the second portal has bought the board nothing.
        for (int i = 0; i < portals.Count; i++)
        {
            for (int j = i + 1; j < portals.Count; j++)
            {
                foreach (int here in new[] { portals[i].A, portals[i].B })
                {
                    foreach (int there in new[] { portals[j].A, portals[j].B })
                    {
                        Check(Math.Abs(Board.ColOf(here) - Board.ColOf(there)) >= 2,
                            $"{name}: the two portals stand at least two files apart");
                    }
                }
            }
        }
    }

    /// <summary>
    /// The pawn move list, audited at every ply of every playout. Two bounds and two
    /// claims about the portal step.
    ///
    /// Eight is the widest the list ever gets and it is the proof the ten-wide stack
    /// buffers rest on: the two expensive shapes are mutually exclusive, because there is
    /// only one opponent. Standing on a mouth with the opponent on the far one gives four
    /// plain steps and up to four landing squares around them; the opponent beside you
    /// instead gives at most five plain moves and the one portal step. One is the audit
    /// that says a null-move rule is not needed — a pawn with a route but no move is a
    /// shape the code tolerates but has never been seen.
    /// </summary>
    private static void CheckPawnMoves(in GameState state, string where)
    {
        List<Move> moves = Collect(state);

        Check(moves.Count is >= 1 and <= 8, $"{where}: {moves.Count} pawn moves, which is outside 1..8");

        var destinations = new HashSet<int>();
        foreach (Move step in moves)
            Check(destinations.Add(step.Cell), $"{where}: {step} is offered twice");

        int standing = state.PawnOf(state.SideToMove);
        if (!state.IsPortalMouth(standing)) return;

        int far = GameState.PortalPartner(standing);

        if (far != state.PawnOf(state.Opponent))
        {
            Check(destinations.Contains(far), $"{where}: standing on a mouth, the free far mouth is not on offer");
            return;
        }

        for (int dir = 0; dir < 4; dir++)
        {
            if (state.Blocked(far, dir)) continue;

            Check(destinations.Contains(far + Board.Delta[dir]),
                $"{where}: the occupied far mouth does not offer every side it has");
        }
    }

    /// <summary>
    /// Whether a wall that leaves both players a route on the board with its portals
    /// erased also leaves them one with the portals in place. It always should: a portal
    /// only ever adds an edge, so the portal graph is a supergraph of the plain one and
    /// reachability cannot go down. The claim runs one way only — a wall can perfectly
    /// well be legal because of a portal — so the reverse is not asserted.
    /// </summary>
    private static bool PortalsOnlyHelp(in GameState state, MoveKind kind, int row, int col)
    {
        GameState plain = state;
        plain.Portals = 0;
        plain.PlaceWallUnchecked(kind, row, col);

        if (!PathFinder.HasPath(plain, 0) || !PathFinder.HasPath(plain, 1)) return true;

        GameState warped = state;
        warped.PlaceWallUnchecked(kind, row, col);

        return PathFinder.HasPath(warped, 0) && PathFinder.HasPath(warped, 1);
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
        int withPortals = 0;

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

            // A portal is two squares or it is nothing, so the exact count is the only
            // honest claim: a roll that asked for two and got one is a board that does not
            // match the title beside it, which is the same silence the counts above exist
            // to break. The five never draws one at all, and the roll and the placement
            // rule have to agree about that independently.
            Check(Board.PopCount(start.PortalMouths()) == 2 * setup.ActualPortals,
                $"{name}: the portals asked for actually fit");

            Check(setup.Size != 5 || !start.HasPortals, $"{name}: a five never carries a portal");

            if (setup.ActualPortals > 0) withPortals++;
        }

        foreach (int size in new[] { 5, 7, Board.Size })
        {
            Check(seen.GetValueOrDefault(size) > 0, $"{size}×{size} comes up in a rolled game");
            Report($"{size}×{size}: {seen.GetValueOrDefault(size)} of 600 rolls");
        }

        Check(withPortals > 0, "a rolled game reaches a portal board");
        Report($"portals: {withPortals} of 600 rolls");
    }

    /// <summary>
    /// Everything the setup screens can ask for, against what the board actually delivers.
    ///
    /// The build sized its draw loops against every square of the game — both goal rows
    /// and both pawn squares included, none of which may be drawn on. On a nine that is a
    /// generous over-count and nothing showed; on a five the reserved squares are two
    /// fifths of the board, and since the draw rejects a reserved square without removing
    /// it, it is still in the bag for the next try. A five asked for ten pickups placed
    /// eight, or four, and <c>Describe()</c> went on saying ten.
    ///
    /// So the claim under test is the one a player can see: the number named is the number
    /// on the board. Not "some were placed" — the exact count, every seed.
    /// </summary>
    private static void BoardCapacity()
    {
        foreach (int size in new[] { Board.Size, 7, 5 })
        {
            // The wall supply is not drawn for, so it changes nothing here; the largest one
            // the size offers is simply the least comfortable board to be sealed on.
            int walls = GameSetup.WallOptions(size)[^1];

            foreach (int holes in GameSetup.HoleOptions(size))
            {
                foreach (int pickups in GameSetup.PickupOptions(size))
                {
                    int wrongHoles = 0, wrongPickups = 0, unplayable = 0;

                    for (int seed = 0; seed < 200; seed++)
                    {
                        var setup = new GameSetup
                        {
                            Size = size, Walls = walls, Holes = holes, Pickups = pickups, Seed = seed,
                        };

                        BuiltBoard built = setup.Build();
                        GameState start = built.State;

                        if (Board.PopCount(built.Holes) != setup.ActualHoles) wrongHoles++;

                        if (Board.PopCount(start.WallPickups | start.SkipPickups) != setup.ActualPickups)
                            wrongPickups++;

                        if (!PathFinder.HasPath(start, 0) || !PathFinder.HasPath(start, 1)) unplayable++;
                    }

                    string name = $"{size}×{size}, {holes} holes, {pickups} pickups";

                    Check(wrongHoles == 0, $"{name}: {wrongHoles} of 200 seeds placed the wrong number of holes");
                    Check(wrongPickups == 0, $"{name}: {wrongPickups} of 200 seeds placed the wrong number of pickups");
                    Check(unplayable == 0, $"{name}: {unplayable} of 200 seeds built a board with no route");
                }
            }
        }

        // The tightest board there is: a five has fourteen drawable squares, so four holes
        // and four pickups is well over half of them.
        var tight = new GameSetup { Size = 5, Walls = 3, Holes = 4, Pickups = 4 };
        Report($"5×5: {tight.DrawableCells} drawable squares, {tight.ActualHoles + tight.ActualPickups} asked for");

        // The case that named this: a five asked for the nine's ten pickups. Fourteen
        // drawable squares is five pairs and room to spare, so the shortfall was never the
        // board running out — it was the draw giving up. It is placeable, and now placed.
        for (int seed = 0; seed < 200; seed++)
        {
            var ten = new GameSetup { Size = 5, Walls = 3, Pickups = 10, Seed = seed };
            GameState start = ten.Build().State;

            if (ten.ActualPickups == 10 &&
                Board.PopCount(start.WallPickups | start.SkipPickups) == 10)
            {
                continue;
            }

            Check(false, $"a five asked for ten pickups and seed {seed} placed " +
                         $"{Board.PopCount(start.WallPickups | start.SkipPickups)}");
            break;
        }

        // Asking for more than the board has must be reported honestly rather than met.
        var greedy = new GameSetup { Size = 5, Holes = 20, Pickups = 20 };
        Check(greedy.ActualHoles == 14 && greedy.ActualPickups == 0,
            "a five gives its fourteen drawable squares to the holes and has none left for pickups");
        Check(greedy.Describe() == "5×5 · 14 holes", $"and says so: \"{greedy.Describe()}\"");

        // An odd count is half a pair, which is no pair at all, and the board it describes
        // is the plain one.
        var odd = new GameSetup { Holes = 1, Pickups = 1 };
        Check(odd.IsStandard && odd.Describe() == "classic", "one hole and one pickup is a classic board");
    }

    /// <summary>
    /// A dozen boards pinned to the numbers they built on the day this was recorded.
    ///
    /// <c>Build</c> draws from a seeded <c>Random</c>, so every board it has ever produced
    /// is a consequence of the exact order in which it asks that Random for numbers. Change
    /// the order — add a draw, move one, filter a list earlier — and nothing else in this
    /// file notices: the board is still symmetric, still playable, still carries the counts
    /// asked for. It is simply a different board from the one a shared link, a saved seed or
    /// a rematch promised, and the two builds no longer agree over a network link.
    ///
    /// Four rows carry no holes and no pickups on purpose. They draw nothing at all, so
    /// they pin the more valuable half of the claim: that work done for boards with
    /// gimmicks on them left the plain boards exactly where they were.
    ///
    /// These are recordings, not laws. A deliberate change to the draw order should break
    /// them and be re-recorded; the point is that it cannot happen quietly.
    /// </summary>
    private static void FrozenBoards()
    {
        (GameSetup Setup, string Encoded, string Holes, ulong Hash)[] table =
        {
            // Nothing to draw for. The seed is consumed and never asked for a number, so
            // these four must not move whatever happens to the scatter.
            (GameSetup.Standard, "9|10|0|0|0", "0", 0xBE1FACE4D1D25BF7),
            (new() { Seed = 7 }, "9|10|0|0|7", "0", 0xBE1FACE4D1D25BF7),
            (new() { Size = 7, Walls = 7, Seed = 13 }, "7|7|0|0|13", "0", 0x9A8827FCCF98E1D8),
            (new() { Size = 5, Walls = 3, Seed = 17 }, "5|3|0|0|17", "0", 0x5D631A2D710BA1F0),

            // Holes only. The hash is the plain board's, because holes are not in it —
            // which is exactly why the mask beside it is recorded too.
            (new() { Holes = 6, Seed = 11 }, "9|10|6|0|11", "200000145000000800", 0xBE1FACE4D1D25BF7),
            (new() { Holes = 10, Seed = 12 }, "9|10|10|0|12", "812004800240090200", 0xBE1FACE4D1D25BF7),

            (new() { Pickups = 6, Seed = 15 }, "9|10|0|6|15", "0", 0x5E35C7AED39CC562),
            (new() { Pickups = 10, Seed = 3 }, "9|10|0|10|3", "0", 0x28A6886CA7A8660B),
            (new() { Holes = 4, Pickups = 6, Seed = 21 }, "9|10|4|6|21", "400020000008000400", 0x4B3D819879657A5D),

            (new() { Size = 7, Walls = 6, Holes = 4, Pickups = 4, Seed = 16 }, "7|6|4|4|16", "800028000200000", 0xBB9FD3F592207761),
            (new() { Size = 5, Walls = 3, Holes = 2, Pickups = 4, Seed = 18 }, "5|3|2|4|18", "4000040000000", 0x1BE12B3DD7693B5A),
            (new() { Size = 5, Walls = 2, Holes = 4, Pickups = 4, Seed = 5 }, "5|2|4|4|5", "1800300000000", 0xC4E24F6F2B52D466),
        };

        foreach ((GameSetup setup, string encoded, string holes, ulong hash) in table)
        {
            Check(setup.Encode() == encoded,
                $"seed {setup.Seed} encodes as {encoded} (built {setup.Encode()})");

            BuiltBoard built = setup.Build();

            // The holes are recorded separately because they are deliberately outside the
            // Zobrist hash — they never change during a game, so they separate no two
            // positions in it. Which also means the hash alone cannot see the hole draw
            // move, and the hole draw is the one this file just rewrote.
            Check(built.Holes.ToString("X") == holes,
                $"{encoded}: holes {holes} (built {built.Holes.ToString("X")})");

            Check(built.State.Hash == hash,
                $"{encoded}: hash 0x{hash:X16} (built 0x{built.State.Hash:X16})");
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

    /// <summary>
    /// A portal, driven directly rather than waited for. It is an ordinary undirected
    /// edge: travelling it is one pawn step to a real square and it passes the turn like
    /// any other, which is the whole reason notation, the network wire and <c>Apply</c>
    /// needed no changes for it at all.
    /// </summary>
    private static void PortalMoves()
    {
        int near = Board.Index(6, 7);
        int far = Board.Index(2, 1);

        Check(GameState.PortalPartner(near) == far, "the two mouths are each other's half-turn image");

        GameState open = GameState.Create(
            pawn0: near, pawn1: Board.Index(0, 4), walls0: 5, walls1: 5, sideToMove: 0);
        open.PlacePortal(far);

        Check(open.IsPortalMouth(near) && open.IsPortalMouth(far), "either end names the pair");
        Check(!open.IsPortalMouth(Board.Index(4, 4)), "and nothing else does");
        Check(Board.PopCount(open.PortalMouths()) == 2, "one portal is two squares");

        var moves = Collect(open);

        Check(moves.Count == 5, $"four steps and the portal, which is five ({moves.Count})");
        Check(moves.Contains(Move.Pawn(2, 1)), "the far mouth is one pawn step away");

        // A portal step inherits none of the free move's debt, which is what lets the
        // search treat the position it reaches as an ordinary child.
        GameState stepped = open;
        stepped.Apply(Move.Pawn(2, 1));

        Check(stepped.PawnOf(0) == far, "the pawn arrives on the far mouth");
        Check(stepped.SideToMove == 1, "and the turn passes like any other step");
        Check(stepped.Ply == open.Ply + 1, "one ply, not a free move");
        Check(stepped.IsPortalMouth(far), "the portal is still there afterwards, and always will be");

        // The opponent standing on the far mouth. The portal edge has no axis, so "hop
        // straight over" has no meaning and every free side of the far mouth is a landing
        // square instead — none of which the four ordinary directions already offered,
        // because the two mouths are four squares apart.
        GameState occupied = GameState.Create(
            pawn0: near, pawn1: far, walls0: 5, walls1: 5, sideToMove: 0);
        occupied.PlacePortal(far);

        var around = Collect(occupied);

        Check(around.Count == 8, $"four steps and the far mouth's four sides, which is eight ({around.Count})");
        Check(around.Count == around.Distinct().Count(), "no square is offered twice");
        Check(!around.Contains(Move.Pawn(2, 1)), "and never the square the opponent is standing on");

        foreach ((int row, int col) in new[] { (1, 1), (3, 1), (2, 0), (2, 2) })
            Check(around.Contains(Move.Pawn(row, col)), $"walking round the far mouth onto {row},{col}");

        // Eight is the widest a pawn move list ever gets and the buffers are ten wide, so
        // this is the case they were sized for. Box the far mouth in on all four sides and
        // the portal is simply not on offer, exactly as a jump with nowhere to land is not.
        GameState boxed = occupied;
        boxed.PlaceWallUnchecked(MoveKind.HorizontalWall, 1, 1);
        boxed.PlaceWallUnchecked(MoveKind.HorizontalWall, 2, 0);
        boxed.PlaceWallUnchecked(MoveKind.VerticalWall, 1, 0);
        boxed.PlaceWallUnchecked(MoveKind.VerticalWall, 2, 1);

        for (int dir = 0; dir < 4; dir++)
            Check(boxed.Blocked(far, dir), "the far mouth really is sealed on all four sides");

        Check(Collect(boxed).Count == 4, "and then the portal offers nothing at all");
    }

    /// <summary>
    /// What a route through a portal costs, on a board built by hand so the answer can be
    /// counted rather than trusted.
    ///
    /// The five below is worth stating plainly, because there is exactly one way the fill
    /// can be wrong and it is off by exactly one. A portal fired against the expanded set
    /// rather than against the accumulated frontier lets a pawn enter a mouth and leave it
    /// in the same breadth-first step, and then every distance through a portal comes out
    /// one short. Nothing else in this file would notice: the route would still be a
    /// route, the board would still be playable, both players would still be treated
    /// alike. The engine would simply believe a lie about how far away the goal is, and
    /// price every wall on the approach against it.
    /// </summary>
    private static void PortalRoutes()
    {
        int near = Board.Index(6, 7);
        int far = Board.Index(2, 1);

        GameState state = GameState.Create(
            pawn0: Board.Index(8, 7), pawn1: Board.Index(0, 7), walls0: 10, walls1: 10, sideToMove: 0);
        state.PlacePortal(far);

        // Two steps up the file to the near mouth, one through it, two more to the goal
        // row. Counted by hand, and the eight below is the same route without the portal.
        Check(PathFinder.Distance(state, 0) == 5,
            $"five steps home through the portal ({PathFinder.Distance(state, 0)})");

        // Player 2 would have to cross six files to reach a mouth, so the portal is worth
        // nothing to them and their route is the plain one down their own file.
        Check(PathFinder.Distance(state, 1) == 8,
            $"and eight for the player the portal does not help ({PathFinder.Distance(state, 1)})");

        Span<byte> distances = stackalloc byte[Board.CellCount];

        for (int player = 0; player < 2; player++)
        {
            PathFinder.FillDistancesToGoal(state, player, distances);
            Check(distances[state.PawnOf(player)] == PathFinder.Distance(state, player),
                $"the fill and the single-source walk agree for player {player + 1}");
        }

        PathFinder.FillDistancesToGoal(state, 0, distances);
        Check(distances[near] == 3 && distances[far] == 2,
            $"the two mouths are one step apart ({distances[near]} and {distances[far]})");

        // A route is a sequence of moves a pawn can really make, so every consecutive pair
        // is either an orthogonal step or the portal edge — and the whole thing is as long
        // as the distance said it would be.
        var route = new List<int>();
        PathFinder.TraceShortestPath(state, 0, distances, route);

        Check(route.Count == PathFinder.Distance(state, 0) + 1,
            $"the traced route is as long as the distance it came from ({route.Count - 1})");
        Check(route[0] == state.PawnOf(0) && Board.RowOf(route[^1]) == state.GoalRow(0),
            "and it runs from the pawn to the goal row");

        int throughPortal = 0;

        for (int i = 1; i < route.Count; i++)
        {
            bool orthogonal = Math.Abs(Board.RowOf(route[i]) - Board.RowOf(route[i - 1])) +
                              Math.Abs(Board.ColOf(route[i]) - Board.ColOf(route[i - 1])) == 1;

            bool warped = state.IsPortalMouth(route[i - 1]) &&
                          route[i] == GameState.PortalPartner(route[i - 1]);

            if (warped) throughPortal++;

            Check(orthogonal || warped, $"step {i} of the route is a move a pawn can actually make");
        }

        Check(throughPortal == 1, "and it goes through the portal exactly once");

        // A portal nobody would walk to changes no distance at all. The fill follows every
        // edge it is handed; being handed a useless one must not shorten anything.
        GameState aside = GameState.Create(
            pawn0: Board.Index(8, 4), pawn1: Board.Index(0, 4), walls0: 0, walls1: 0, sideToMove: 0);
        aside.PlacePortal(Board.Index(3, 0));

        Check(PathFinder.Distance(aside, 0) == 8 && PathFinder.Distance(aside, 1) == 8,
            "a portal too far off the walking line to be worth using changes nothing");

        // The wall search reads the route, so a route that leaves through a portal has to
        // put the slots beside its far half on the candidate list. Without the portal tail
        // in SlotsAlongRoute the walk stops dead at the mouth, and "a wall on the portal
        // approach adds two to their trip" becomes a move the engine cannot see at all.
        // The corner counted below is reached by nothing else on this board.
        GameState defending = GameState.Create(
            pawn0: Board.Index(8, 7), pawn1: Board.Index(0, 7), walls0: 10, walls1: 10, sideToMove: 1);
        defending.PlacePortal(far);

        GameState erased = defending;
        erased.Portals = 0;

        int offered = FarCornerWalls(defending);

        Check(offered > 0, $"walls beyond the portal are offered as candidates ({offered} of them)");
        Check(FarCornerWalls(erased) == 0, "and are not, on the same board with the portal taken out");
    }

    /// <summary>
    /// How many candidate walls the engine offers in the corner of the board that only the
    /// far half of a portal route reaches. Counted through <c>Generate</c> rather than
    /// through the route walk itself, because that is where it matters: a slot the route
    /// never collected is a wall the search never considers, at any depth.
    /// </summary>
    private static int FarCornerWalls(in GameState state)
    {
        Span<Move> moves = stackalloc Move[MoveCandidates.MaxMoves];
        int count = MoveCandidates.Generate(state, moves, MoveCandidates.MaxWalls, scoreWalls: true);

        int found = 0;
        for (int i = 0; i < count; i++)
            if (moves[i].IsWall && moves[i].Row <= 2 && moves[i].Col <= 1) found++;

        return found;
    }

    /// <summary>
    /// Portals are permanent, and that is the whole reason they are outside the Zobrist
    /// hash: they never change, so two positions inside one game always share them and
    /// they separate nothing a transposition table needs separated. It is an invariant
    /// rather than a happy accident, so it is pinned here instead of relied on.
    ///
    /// What would change it: make a portal one-shot, or give it a cooldown, and it
    /// acquires state. Then it needs a <c>Zobrist.Portal</c> table of 41 entries indexed
    /// by the pair's low cell, XORed on every change. That is the same edit that would
    /// break the repetition check, because two positions agreeing on pawns, walls,
    /// supplies and side to move would no longer be the same position. The two are one
    /// fact, and this check is where breaking it should first show up.
    /// </summary>
    private static void PortalEffects()
    {
        GameState portal = GameState.Create(
            pawn0: Board.Index(6, 7), pawn1: Board.Index(0, 4), walls0: 5, walls1: 5, sideToMove: 0);
        portal.PlacePortal(Board.Index(2, 1));

        GameState erased = portal;
        erased.Portals = 0;

        Check(portal.Hash == erased.Hash, "a portal board hashes as the same board without one");
        Check(portal.HasPortals && !erased.HasPortals, "which is not because the two boards are the same board");
        Check(PathFinder.Distance(portal, 0) != PathFinder.Distance(erased, 0),
            "nor because the portal makes no difference to how the position plays");

        GameState both = portal;
        both.PlacePortal(Board.Index(3, 2));

        Check(both.Hash == portal.Hash, "and a second portal changes nothing either");

        // A portal step is an ordinary pawn move onto a real square, so the position it
        // reaches is exactly the position it looks like — which is what lets the table
        // answer one with the other, and what kept Notation and the network wire unchanged.
        GameState stepped = portal;
        stepped.Apply(Move.Pawn(2, 1));

        GameState directly = GameState.Create(
            pawn0: Board.Index(2, 1), pawn1: Board.Index(0, 4), walls0: 5, walls1: 5, sideToMove: 1);
        directly.PlacePortal(Board.Index(2, 1));

        Check(stepped.Hash == directly.Hash, "a position reached through a portal hashes as itself");
        Check(stepped.Hash != portal.Hash, "and moving the pawn still moves the hash");
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

    // -------------------------------------------------------- race verdict --

    private const byte RaceUnresolved = 0;

    /// <summary>
    /// The wall-less race verdict is returned above <see cref="Evaluation.MateThreshold"/>,
    /// which ends iterative deepening — a wrong one is played to the end and never
    /// reconsidered. It used to allow a single move of slack, on the argument that
    /// jumping is the only way the pawns interact. It is not: a pawn in a corridor
    /// whose jump square is walled off cannot be passed at all, and that costs more
    /// than a jump saves.
    ///
    /// With every wall spent the rest of the game is a finite two-pawn game, so this
    /// solves it outright and holds the verdict to the answer.
    /// </summary>
    private static void RaceVerdict()
    {
        GameSetup[] shapes =
        {
            new() { Size = 9, Walls = 10, Seed = 41 },
            new() { Size = 9, Walls = 10, Holes = 6, Seed = 42 },
            new() { Size = 9, Walls = 10, Seed = 45 },
            new() { Size = 9, Walls = 10, Holes = 10, Seed = 46 },
            new() { Size = 7, Walls = 6, Holes = 4, Seed = 43 },
            new() { Size = 7, Walls = 7, Seed = 47 },
            new() { Size = 5, Walls = 3, Seed = 44 },
        };

        int audited = 0, settled = 0, wrong = 0;

        foreach (GameSetup shape in shapes)
        {
            if (!TrySpendWalls(shape, out GameState spent)) continue;

            var states = new List<GameState>();
            var successors = new List<int[]>();
            ExploreRace(spent, states, successors);

            byte[] outcome = SolveRace(states, successors);

            for (int i = 0; i < states.Count; i++)
            {
                GameState state = states[i];
                if (state.IsGameOver || outcome[i] == RaceUnresolved) continue;

                audited++;

                int verdict = Evaluation.RaceScore(state, 0);
                if (verdict == Evaluation.Unknown) continue;

                settled++;
                if (verdict > 0 != (outcome[i] == state.SideToMove + 1)) wrong++;
            }
        }

        Report($"{audited:N0} solved positions, {settled:N0} of them called by the verdict");
        Check(audited > 20_000, "the audit reaches a meaningful number of positions");
        Check(settled * 2 > audited, "the margin still settles most of the race");
        Check(wrong == 0, $"the race verdict never names the wrong winner ({wrong} did)");
    }

    /// <summary>
    /// The same question asked of a portal board, in two parts.
    ///
    /// First the guard. A verdict is returned above <see cref="Evaluation.MateThreshold"/>
    /// and ends iterative deepening, so a wrong one is played to the end and never looked
    /// at again — and the margin that makes it safe was measured against pawns obstructing
    /// each other. A portal is that case in its most extreme form: two mouths are a
    /// corridor of degree one, and a pawn on the far mouth with all four of its sides
    /// walled cannot be passed at all, while the fill treats pawns as transparent and
    /// routes straight through. So the engine declines to give a verdict on a portal
    /// board, and that is driven here from outside on a position the verdict would
    /// otherwise be delighted to call.
    ///
    /// Then the measurement the guard is waiting on, run with the machinery that produced
    /// the 3: solve the wall-spent pawn game exactly on portal boards and count how often
    /// each margin would name the wrong winner. Reported rather than acted on — changing
    /// the guard is not this file's decision, and a number nobody has looked at is not a
    /// reason to take a guard off.
    /// </summary>
    private static void PortalRaceVerdict()
    {
        // Player 1 is five steps out on the plain board and four through the portal, and
        // player 2 is eight either way, so the verdict speaks on both. The two boards hash
        // identically — that is the point of PortalEffects — so they need two agents, or
        // the second search would be answered out of the first one's table.
        GameState warped = GameState.Create(
            pawn0: Board.Index(5, 7), pawn1: Board.Index(0, 7), walls0: 0, walls1: 0, sideToMove: 0);
        warped.PlacePortal(Board.Index(2, 1));

        GameState plain = warped;
        plain.Portals = 0;

        Check(Evaluation.RaceScore(plain, 0) != Evaluation.Unknown, "the verdict would call the plain position");
        Check(Evaluation.RaceScore(warped, 0) != Evaluation.Unknown, "and the portal position just as readily");

        var budget = TimeSpan.FromSeconds(5);

        var onPlain = new SearchAgent(maxDepth: 4, moveTime: budget, threads: 1);
        onPlain.ChooseMove(plain);

        var onWarped = new SearchAgent(maxDepth: 4, moveTime: budget, threads: 1);
        onWarped.ChooseMove(warped);

        // Four plies is far too shallow to reach a real win from five steps out, so a
        // score this large can only have come from the verdict.
        Check(onPlain.LastResult.Score >= Evaluation.MateThreshold,
            $"the search takes the verdict without portals ({onPlain.LastResult.Score})");
        Check(onWarped.LastResult.Score < Evaluation.MateThreshold,
            $"and declines it on the same position with one ({onWarped.LastResult.Score})");

        // ---- and what the margin would have to be, if the guard ever came off ----

        GameSetup[] shapes =
        {
            new() { Size = 9, Walls = 10, Portals = 2, Seed = 51 },
            new() { Size = 9, Walls = 10, Portals = 1, Seed = 52 },
            new() { Size = 9, Walls = 10, Holes = 6, Portals = 2, Seed = 53 },
            new() { Size = 7, Walls = 6, Portals = 1, Seed = 54 },
            new() { Size = 7, Walls = 7, Portals = 1, Seed = 55 },
            new() { Size = 9, Walls = 10, Portals = 2, Seed = 56 },
            new() { Size = 9, Walls = 14, Portals = 1, Seed = 57 },
            new() { Size = 9, Walls = 10, Holes = 10, Portals = 2, Seed = 58 },
            new() { Size = 7, Walls = 6, Holes = 4, Portals = 1, Seed = 59 },
            new() { Size = 7, Walls = 7, Portals = 1, Seed = 60 },
        };

        const int widest = 8;

        var called = new int[widest + 1];
        var wrong = new int[widest + 1];
        int audited = 0, boards = 0;

        foreach (GameSetup shape in shapes)
        {
            if (!TrySpendWalls(shape, out GameState spent)) continue;

            Check(spent.HasPortals, $"seed {shape.Seed}: the solved board still carries its portals");
            boards++;

            var states = new List<GameState>();
            var successors = new List<int[]>();
            ExploreRace(spent, states, successors);

            byte[] outcome = SolveRace(states, successors);

            for (int i = 0; i < states.Count; i++)
            {
                GameState state = states[i];
                if (state.IsGameOver || outcome[i] == RaceUnresolved) continue;

                int mine = PathFinder.Distance(state, state.SideToMove);
                int theirs = PathFinder.Distance(state, state.SideToMove ^ 1);
                if (mine < 0 || theirs < 0) continue;

                audited++;
                bool moverWins = outcome[i] == state.SideToMove + 1;

                // The verdict's own rule, with the margin left open. Everything else about
                // it — that it is exact when it speaks, that it outranks the mate
                // threshold — is unchanged.
                for (int margin = 1; margin <= widest; margin++)
                {
                    bool callsMover = mine <= theirs - margin;
                    bool callsOther = mine >= theirs + margin + 1;

                    if (!callsMover && !callsOther) continue;

                    called[margin]++;
                    if (callsMover != moverWins) wrong[margin]++;
                }
            }
        }

        Check(boards == shapes.Length, $"{boards} of {shapes.Length} portal boards spent their walls");
        Check(audited > 5_000, $"the audit reaches a meaningful number of positions ({audited:N0})");

        int clean = 0;

        for (int margin = 1; margin <= widest && clean == 0; margin++)
        {
            Report($"margin {margin}: {called[margin]:N0} verdicts, {wrong[margin]:N0} of them wrong");
            if (wrong[margin] == 0) clean = margin;
        }

        Report($"{audited:N0} solved portal positions across {boards} boards");

        // Stated carefully on purpose. The 3 in use was pinned over two million positions;
        // this audit is a fortieth of that, which is enough to see a margin fail and
        // nowhere near enough to certify one. So the reading is a floor, never a licence.
        Report(clean == 0
            ? $"nothing up to {widest} was clean here, so the guard stays"
            : $"the smallest margin clean over this sample is {clean}, against the " +
              $"{Evaluation.RaceMargin} a plain board uses — a floor, not a licence: this " +
              "audit is far too small to shorten a margin measured over two million");
    }

    /// <summary>Plays random legal moves until neither player has a wall left.</summary>
    private static bool TrySpendWalls(GameSetup shape, out GameState state)
    {
        var random = new Random(shape.Seed);
        state = shape.Build().State;

        for (int step = 0; step < 400; step++)
        {
            if (state.IsGameOver) return false;
            if (state.WallsOf(0) == 0 && state.WallsOf(1) == 0) return true;

            List<Move> legal = state.LegalMoves();
            if (legal.Count == 0) return false;

            // Lean on walls so the supplies empty before anyone gets home, and so the
            // board ends up with the corridors that pawn blocking needs.
            List<Move> walls = legal.FindAll(move => move.IsWall);

            state.Apply(walls.Count > 0 && random.Next(100) < 85
                ? walls[random.Next(walls.Count)]
                : legal[random.Next(legal.Count)]);
        }

        return false;
    }

    /// <summary>Every position the two pawns can still reach, and how they connect.</summary>
    private static void ExploreRace(GameState start, List<GameState> states, List<int[]> successors)
    {
        var index = new Dictionary<(int, int, int), int>();
        var queue = new Queue<int>();

        Add(start);

        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            GameState state = states[current];

            if (state.IsGameOver)
            {
                successors[current] = Array.Empty<int>();
                continue;
            }

            List<Move> moves = Collect(state);
            var next = new int[moves.Count];

            for (int i = 0; i < moves.Count; i++)
            {
                GameState child = state;
                child.Apply(moves[i]);
                next[i] = Add(child);
            }

            successors[current] = next;
        }

        int Add(in GameState state)
        {
            (int, int, int) key = (state.PawnOf(0), state.PawnOf(1), state.SideToMove);
            if (index.TryGetValue(key, out int existing)) return existing;

            int id = states.Count;
            index[key] = id;
            states.Add(state);
            successors.Add(Array.Empty<int>());
            queue.Enqueue(id);
            return id;
        }
    }

    /// <summary>
    /// Retrograde analysis: start from the finished games and work backwards. A
    /// position is won for the side to move as soon as one move reaches a win, and
    /// lost once every move has been shown to lose. Anything still unmarked at the end
    /// is a position both players can avoid losing forever.
    /// </summary>
    private static byte[] SolveRace(List<GameState> states, List<int[]> successors)
    {
        int count = states.Count;

        var outcome = new byte[count];
        var untried = new int[count];
        var predecessors = new List<int>[count];

        for (int i = 0; i < count; i++) predecessors[i] = new List<int>();

        for (int i = 0; i < count; i++)
        {
            untried[i] = successors[i].Length;
            foreach (int child in successors[i]) predecessors[child].Add(i);
        }

        var queue = new Queue<int>();

        for (int i = 0; i < count; i++)
        {
            int winner = states[i].Winner;
            if (winner < 0) continue;

            outcome[i] = (byte)(winner + 1);
            queue.Enqueue(i);
        }

        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            byte result = outcome[current];

            foreach (int parent in predecessors[current])
            {
                if (outcome[parent] != RaceUnresolved) continue;

                var parentWins = (byte)(states[parent].SideToMove + 1);

                if (result == parentWins) outcome[parent] = parentWins;
                else if (--untried[parent] == 0) outcome[parent] = result;
                else continue;

                queue.Enqueue(parent);
            }
        }

        return outcome;
    }

    // ---------------------------------------------------------------- harness --

    private static List<Move> Collect(in GameState state)
    {
        Span<Move> buffer = stackalloc Move[10];
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
