using System.Numerics;
using Quoridor.Core;

namespace Quoridor.Engine;

/// <summary>
/// Builds and orders the move list a search actually wants to look at.
///
/// Of the ~128 wall placements available at a typical node the overwhelming majority
/// are irrelevant: a wall only matters if it touches a square someone is about to walk
/// through, or if it extends a barrier that already exists. Restricting candidates to
/// those shrinks the branching factor from ~130 to ~30 with almost no loss of strength,
/// which is what makes deep search affordable.
///
/// Nothing here allocates — the caller supplies the destination span out of the
/// search's move stack, and the working sets live on the stack.
/// </summary>
internal static class MoveCandidates
{
    /// <summary>Upper bound on walls this will ever emit for one node.</summary>
    public const int MaxWalls = 48;

    /// <summary>Size a per-ply slice of the move stack must have.</summary>
    public const int MaxMoves = 8 + MaxWalls;

    /// <summary>
    /// Writes legal moves for the side to move into <paramref name="destination"/>,
    /// best-looking first, and returns the count.
    /// </summary>
    /// <param name="scoreWalls">
    /// When true, walls are ranked by how much they actually lengthen the opponent's
    /// route (two flood fills each). Worth it near the root, too slow deep in the tree,
    /// where a static proximity score orders nearly as well for a fraction of the cost.
    /// </param>
    public static int Generate(in GameState state, Span<Move> destination, int maxWalls, bool scoreWalls)
    {
        int mover = state.SideToMove;
        int opponent = mover ^ 1;
        int count = 0;

        Span<byte> myDistances = stackalloc byte[Board.CellCount];
        PathFinder.FillDistancesToGoal(state, mover, myDistances);

        count += GeneratePawnMoves(state, destination, myDistances);

        maxWalls = Math.Min(maxWalls, MaxWalls);
        if (maxWalls <= 0 || state.WallsOf(mover) == 0) return count;

        Span<byte> theirDistances = stackalloc byte[Board.CellCount];
        PathFinder.FillDistancesToGoal(state, opponent, theirDistances);

        ulong candidateSlots = CollectCandidateSlots(state, mover, opponent, myDistances, theirDistances);
        if (candidateSlots == 0) return count;

        Span<Move> walls = stackalloc Move[2 * Board.SlotCount];
        Span<int> scores = stackalloc int[2 * Board.SlotCount];

        int wallCount = ScoreWalls(
            state, mover, opponent, candidateSlots, scoreWalls, myDistances, theirDistances, walls, scores);

        count += TakeBest(walls, scores, wallCount, maxWalls, destination[count..]);
        return count;
    }

    // ================================================================== pawns ==

    private static int GeneratePawnMoves(in GameState state, Span<Move> destination, Span<byte> myDistances)
    {
        Span<Move> buffer = stackalloc Move[8];
        int count = state.GeneratePawnMoves(buffer);

        Span<int> keys = stackalloc int[8];
        for (int i = 0; i < count; i++) keys[i] = myDistances[buffer[i].Cell];

        // Selection sort over at most five entries: closest to goal first.
        for (int i = 0; i < count; i++)
        {
            int best = i;
            for (int j = i + 1; j < count; j++)
                if (keys[j] < keys[best]) best = j;

            (buffer[i], buffer[best]) = (buffer[best], buffer[i]);
            (keys[i], keys[best]) = (keys[best], keys[i]);

            destination[i] = buffer[i];
        }

        return count;
    }

    // ================================================================== walls ==

    private static ulong CollectCandidateSlots(
        in GameState state,
        int mover,
        int opponent,
        Span<byte> myDistances,
        Span<byte> theirDistances)
    {
        ulong slots = 0;

        // Squares both players are about to walk through.
        slots |= SlotsAlongRoute(state, mover, myDistances);
        slots |= SlotsAlongRoute(state, opponent, theirDistances);

        // Right in front of the opponent, even if it is off their current best route.
        slots |= SlotsTouching(state.PawnOf(opponent));

        // Walls tend to be built in chains, so the squares around existing walls are
        // where the next one usually belongs.
        ulong occupied = state.HorizontalWalls | state.VerticalWalls;
        while (occupied != 0)
        {
            int slot = BitOperations.TrailingZeroCount(occupied);
            occupied &= occupied - 1;

            int row = slot / Board.SlotSize;
            int col = slot % Board.SlotSize;

            for (int dr = -1; dr <= 1; dr++)
            {
                for (int dc = -1; dc <= 1; dc++)
                {
                    if (Board.SlotInBounds(row + dr, col + dc))
                        slots |= 1UL << Board.SlotIndex(row + dr, col + dc);
                }
            }
        }

        return slots;
    }

    private static ulong SlotsAlongRoute(in GameState state, int player, Span<byte> distances)
    {
        ulong slots = 0;

        int cell = state.PawnOf(player);
        if (distances[cell] == PathFinder.Unreachable) return 0;

        slots |= SlotsTouching(cell);

        // Walk one shortest route to its goal, collecting the slots beside it.
        while (distances[cell] > 0)
        {
            int next = -1;
            int best = distances[cell];

            for (int dir = 0; dir < 4; dir++)
            {
                if (state.Blocked(cell, dir)) continue;

                int neighbour = cell + Board.Delta[dir];
                int d = distances[neighbour];
                if (d != PathFinder.Unreachable && d < best)
                {
                    best = d;
                    next = neighbour;
                }
            }

            if (next < 0) break;

            cell = next;
            slots |= SlotsTouching(cell);
        }

        return slots;
    }

    /// <summary>The (up to four) wall slots whose walls can block movement beside a cell.</summary>
    private static ulong SlotsTouching(int cell)
    {
        int row = Board.RowOf(cell);
        int col = Board.ColOf(cell);
        ulong mask = 0;

        for (int r = row - 1; r <= row; r++)
        {
            for (int c = col - 1; c <= col; c++)
            {
                if (Board.SlotInBounds(r, c))
                    mask |= 1UL << Board.SlotIndex(r, c);
            }
        }

        return mask;
    }

    /// <summary>
    /// Walks the candidate slots, keeps the legal walls and scores them.
    ///
    /// The trick that makes this cheap: a wall can only change a player's distance if it
    /// closes an edge between two squares whose distances differ. Both distance maps are
    /// already in hand, so that is four array reads — against a flood fill, which is what
    /// the naive version paid for every candidate. A wall touching neither player's
    /// progress is legal, costs nothing, and needs no path work at all; most candidates
    /// turn out to be exactly that.
    /// </summary>
    private static int ScoreWalls(
        in GameState state,
        int mover,
        int opponent,
        ulong candidateSlots,
        bool scoreWalls,
        Span<byte> myDistances,
        Span<byte> theirDistances,
        Span<Move> walls,
        Span<int> scores)
    {
        // Free: the maps were already built for move ordering.
        int baselineMine = myDistances[state.PawnOf(mover)];
        int baselineTheirs = theirDistances[state.PawnOf(opponent)];

        int opponentPawn = state.PawnOf(opponent);
        int opponentRow = Board.RowOf(opponentPawn);
        int opponentCol = Board.ColOf(opponentPawn);

        int count = 0;

        while (candidateSlots != 0)
        {
            int slot = BitOperations.TrailingZeroCount(candidateSlots);
            candidateSlots &= candidateSlots - 1;

            int row = slot / Board.SlotSize;
            int col = slot % Board.SlotSize;

            for (int orientation = 0; orientation < 2; orientation++)
            {
                MoveKind kind = orientation == 0 ? MoveKind.HorizontalWall : MoveKind.VerticalWall;

                if (!state.IsSlotFree(kind, row, col)) continue;

                bool touchesMine = ClosesProgress(kind, row, col, myDistances);
                bool touchesTheirs = ClosesProgress(kind, row, col, theirDistances);

                int score;

                if (scoreWalls)
                {
                    // Here the routes have to be measured anyway, so the progress test
                    // pays for itself twice: it skips the measurement where nothing can
                    // have moved, and where something did move the new distance doubles
                    // as the legality check — a sealed-in player has none.
                    int mine = baselineMine;
                    int theirs = baselineTheirs;

                    if (touchesMine || touchesTheirs)
                    {
                        GameState placed = state;
                        placed.PlaceWallUnchecked(kind, row, col);

                        if (touchesTheirs)
                        {
                            theirs = PathFinder.Distance(placed, opponent);
                            if (theirs < 0) continue;
                        }

                        if (touchesMine)
                        {
                            mine = PathFinder.Distance(placed, mover);
                            if (mine < 0) continue;
                        }
                    }

                    int gain = theirs - baselineTheirs;

                    // A wall that gains the opponent nothing ranks below every wall that
                    // does, whatever it happens to cost us.
                    score = gain > 0
                        ? gain * 24 - (mine - baselineMine) * 32
                        : gain * 24 - 400;
                }
                else
                {
                    // Deep in the tree nobody asks what the wall does, only whether it is
                    // legal — and measuring a route to answer that would cost far more
                    // than it saves. Two cheap filters in series: nothing can be sealed
                    // if no route even changed, and nothing can be sealed by a wall that
                    // fails the wall-graph test either.
                    if ((touchesMine || touchesTheirs) && WallGraph.CanDisconnect(state, kind, row, col))
                    {
                        GameState placed = state;
                        placed.PlaceWallUnchecked(kind, row, col);

                        if (touchesTheirs && !PathFinder.HasPath(placed, opponent)) continue;
                        if (touchesMine && !PathFinder.HasPath(placed, mover)) continue;
                    }

                    // Cheap stand-in for the ordering: walls near the opponent's pawn
                    // tend to be the ones worth trying first.
                    int distance = Math.Max(Math.Abs(row - opponentRow), Math.Abs(col - opponentCol));
                    score = -distance;
                }

                walls[count] = new Move(kind, row, col);
                scores[count] = score;
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Whether this wall closes an edge that some shortest route actually uses.
    ///
    /// Between two connected squares a breadth-first distance differs by exactly one or
    /// not at all. An edge joining two squares of equal distance is on nobody's shortest
    /// route, so closing it cannot change any distance — and if neither of a wall's two
    /// edges is a step, nothing on the board moves and nobody can have been sealed in.
    /// </summary>
    internal static bool ClosesProgress(MoveKind kind, int row, int col, Span<byte> distances)
    {
        return kind == MoveKind.HorizontalWall
            ? IsStep(distances, Board.Index(row, col), Board.Index(row + 1, col)) ||
              IsStep(distances, Board.Index(row, col + 1), Board.Index(row + 1, col + 1))
            : IsStep(distances, Board.Index(row, col), Board.Index(row, col + 1)) ||
              IsStep(distances, Board.Index(row + 1, col), Board.Index(row + 1, col + 1));
    }

    private static bool IsStep(Span<byte> distances, int from, int to)
    {
        byte a = distances[from];
        byte b = distances[to];

        // An unreachable square breaks the assumption behind this test, so call it a step
        // and let the full check decide.
        if (a == PathFinder.Unreachable || b == PathFinder.Unreachable) return true;

        return a != b;
    }

    /// <summary>
    /// Legality check for a single move, using the <see cref="WallGraph"/> fast path.
    /// Equivalent to <see cref="GameState.IsLegal"/> but cheaper, which matters because
    /// the search validates table and killer moves at every node.
    /// </summary>
    public static bool IsLegal(in GameState state, Move move)
    {
        if (move.Kind == MoveKind.Pawn)
            return state.IsPawnMoveLegal(move.Row, move.Col);

        if (!Board.SlotInBounds(move.Row, move.Col)) return false;
        if (state.WallsOf(state.SideToMove) == 0) return false;

        return TryPlace(state, move.Kind, move.Row, move.Col, out _);
    }

    /// <summary>
    /// Full legality for a wall, using <see cref="WallGraph"/> to skip the flood fills
    /// whenever the placement provably cannot cut the board.
    /// </summary>
    private static bool TryPlace(in GameState state, MoveKind kind, int row, int col, out GameState placed)
    {
        placed = default;

        if (!state.IsSlotFree(kind, row, col)) return false;

        GameState probe = state;
        probe.PlaceWallUnchecked(kind, row, col);

        if (WallGraph.CanDisconnect(state, kind, row, col))
        {
            if (!PathFinder.HasPath(probe, 0)) return false;
            if (!PathFinder.HasPath(probe, 1)) return false;
        }

        placed = probe;
        return true;
    }

    /// <summary>Moves the <paramref name="take"/> highest-scoring walls into the destination.</summary>
    private static int TakeBest(Span<Move> walls, Span<int> scores, int count, int take, Span<Move> destination)
    {
        take = Math.Min(take, count);

        for (int i = 0; i < take; i++)
        {
            int best = i;
            for (int j = i + 1; j < count; j++)
                if (scores[j] > scores[best]) best = j;

            (walls[i], walls[best]) = (walls[best], walls[i]);
            (scores[i], scores[best]) = (scores[best], scores[i]);

            destination[i] = walls[i];
        }

        return take;
    }
}
