using System.Runtime.CompilerServices;

namespace Quoridor.Core;

/// <summary>
/// Reachability and shortest-path queries, implemented as a breadth-first flood
/// fill over bitboards. One expansion step advances the whole frontier in four
/// shifts and four ANDs, so a full-board search costs a few hundred nanoseconds —
/// which matters because wall legality calls this twice for every one of the
/// ~128 candidate walls at every search node.
///
/// Pawns are treated as transparent: because a pawn may always be jumped, the
/// other pawn never lengthens a path by more than a transient step, and every
/// strong Quoridor evaluation ignores it.
/// </summary>
public static class PathFinder
{
    /// <summary>Grows a set of cells by one step in every direction the walls allow.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static UInt128 Expand(in GameState state, UInt128 frontier)
    {
        UInt128 next = frontier;
        next |= (frontier & ~state.BlockedNorth) >> Board.Size;
        next |= (frontier & ~state.BlockedSouth) << Board.Size;
        next |= (frontier & ~state.BlockedWest) >> 1;
        next |= (frontier & ~state.BlockedEast) << 1;
        return next & Board.All;
    }

    /// <summary>True when the player can still reach their goal row.</summary>
    public static bool HasPath(in GameState state, int player)
    {
        UInt128 goal = Board.GoalMask(player);
        UInt128 reached = Board.Bit(state.PawnOf(player));

        while ((reached & goal) == 0)
        {
            UInt128 next = Expand(state, reached);
            if (next == reached) return false;
            reached = next;
        }

        return true;
    }

    /// <summary>
    /// Number of steps from the player's pawn to their goal row, or -1 if walled off.
    /// </summary>
    public static int Distance(in GameState state, int player)
    {
        UInt128 goal = Board.GoalMask(player);
        UInt128 reached = Board.Bit(state.PawnOf(player));
        if ((reached & goal) != 0) return 0;

        for (int distance = 1; distance <= Board.CellCount; distance++)
        {
            UInt128 next = Expand(state, reached);
            if (next == reached) return -1;
            if ((next & goal) != 0) return distance;
            reached = next;
        }

        return -1;
    }

    /// <summary>
    /// Fills <paramref name="distances"/> (length 81) with each cell's step count to
    /// the player's goal row. Unreachable cells get <see cref="Unreachable"/>.
    /// A single fill answers "which neighbour is on a shortest path" for every cell,
    /// which is what the bot walks and what the evaluation reads.
    /// </summary>
    public const byte Unreachable = 255;

    public static void FillDistancesToGoal(in GameState state, int player, Span<byte> distances)
    {
        distances.Fill(Unreachable);

        UInt128 reached = Board.GoalMask(player);
        UInt128 previous = 0;
        byte distance = 0;

        while (reached != previous)
        {
            UInt128 layer = reached & ~previous;
            while (layer != 0)
            {
                int cell = Board.LowestBit(layer);
                distances[cell] = distance;
                layer &= layer - UInt128.One;
            }

            previous = reached;
            reached = Expand(state, reached);
            distance++;
        }
    }

    /// <summary>
    /// Walks one shortest route from the player's pawn to their goal row and appends
    /// the cells (pawn cell first, goal cell last) to <paramref name="cells"/>.
    /// Used to focus wall search on the squares that actually matter.
    /// </summary>
    public static void TraceShortestPath(in GameState state, int player, Span<byte> distances, List<int> cells)
    {
        int cell = state.PawnOf(player);
        if (distances[cell] == Unreachable) return;

        cells.Add(cell);

        while (distances[cell] > 0)
        {
            int next = -1;
            int bestDistance = distances[cell];

            for (int dir = 0; dir < 4; dir++)
            {
                if (state.Blocked(cell, dir)) continue;

                int neighbour = cell + Board.Delta[dir];
                int d = distances[neighbour];
                if (d != Unreachable && d < bestDistance)
                {
                    bestDistance = d;
                    next = neighbour;
                }
            }

            if (next < 0) break;

            cell = next;
            cells.Add(cell);
        }
    }

    /// <summary>
    /// The pawn step that makes the most progress toward the player's goal, or null
    /// when the player is sealed off. Considers only plain steps plus jumps, so the
    /// result is always a legal move for the side to move.
    /// </summary>
    public static Move? BestStepTowardGoal(in GameState state, int player, Span<byte> distances)
    {
        Span<Move> buffer = stackalloc Move[8];
        int count = state.GeneratePawnMoves(buffer);

        Move best = default;
        int bestDistance = int.MaxValue;

        for (int i = 0; i < count; i++)
        {
            int d = distances[buffer[i].Cell];
            if (d == Unreachable) continue;

            if (d < bestDistance)
            {
                bestDistance = d;
                best = buffer[i];
            }
        }

        return bestDistance == int.MaxValue ? null : best;
    }
}
