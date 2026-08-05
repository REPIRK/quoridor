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
///
/// A portal is an ordinary undirected edge, so every query here follows it and no
/// caller needs a second, portal-aware distance of its own. Boards without portals
/// take a separate compiled path and pay nothing for the ones that have them.
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

    /// <summary>
    /// The four directions plus the portal edges. A portal is an edge like any other, but
    /// it only ever has to be followed once: the frontier never shrinks, so once a mouth is
    /// in it its partner is in it forever. <c>_pending</c> holds the mouths that have not
    /// fired, which makes the whole thing one AND against a register per step and one short
    /// loop per portal per fill — not per portal per step. That loop-carried state is why
    /// the portal edge lives here and not inside <see cref="Expand"/>.
    ///
    /// The firing is applied to <paramref name="frontier"/>, the accumulated set at distance
    /// at most k-1, and never to the expanded set. Doing it after <see cref="Expand"/> would
    /// let a pawn enter a mouth and leave it in the same step, and every distance through a
    /// portal would come out short by one — an error nothing else in the project would
    /// notice, because the route would still be a route. Every caller passes the set it has
    /// accumulated so far and that set is monotone, so firing a mouth exactly once is both
    /// sufficient and correct.
    ///
    /// This was first written as one generic walk over an IStepper, so that a plain board
    /// and a portal board would share a body and the JIT would specialise each. It did not:
    /// the shortest-path query went from 29 ns to 35, and since wall legality runs it twice
    /// for each of ~30 candidate walls at every node, the engine lost about a third of its
    /// nodes per second on boards with no portals at all. So the plain walk below is the
    /// original code, untouched and un-generic, and this type is only ever reached when
    /// there is actually a portal to follow.
    /// </summary>
    private struct Warped
    {
        private UInt128 _pending;

        public Warped(in GameState state) => _pending = state.PortalMouths();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public UInt128 Step(in GameState state, UInt128 frontier)
        {
            UInt128 next = Expand(state, frontier);

            UInt128 hit = frontier & _pending;
            if (hit == 0) return next;

            _pending &= ~hit;

            do
            {
                int cell = Board.LowestBit(hit);
                hit &= hit - UInt128.One;

                UInt128 partner = Board.Bit(GameState.PortalPartner(cell));
                next |= partner;

                // Both ends of a fired portal are done; without this the partner refires
                // when the walk reaches it, which is harmless but not free.
                _pending &= ~partner;
            }
            while (hit != 0);

            return next;
        }
    }

    /// <summary>True when the player can still reach their goal row.</summary>
    public static bool HasPath(in GameState state, int player)
    {
        if (state.HasPortals) return HasPathThroughPortals(state, player);

        UInt128 goal = state.GoalMask(player);
        UInt128 reached = Board.Bit(state.PawnOf(player));

        while ((reached & goal) == 0)
        {
            UInt128 next = Expand(state, reached);
            if (next == reached) return false;
            reached = next;
        }

        return true;
    }

    private static bool HasPathThroughPortals(in GameState state, int player)
    {
        UInt128 goal = state.GoalMask(player);
        UInt128 reached = Board.Bit(state.PawnOf(player));
        var warp = new Warped(state);

        while ((reached & goal) == 0)
        {
            UInt128 next = warp.Step(state, reached);
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
        if (state.HasPortals) return DistanceThroughPortals(state, player);

        UInt128 goal = state.GoalMask(player);
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

    private static int DistanceThroughPortals(in GameState state, int player)
    {
        UInt128 goal = state.GoalMask(player);
        UInt128 reached = Board.Bit(state.PawnOf(player));
        if ((reached & goal) != 0) return 0;

        var warp = new Warped(state);

        for (int distance = 1; distance <= Board.CellCount; distance++)
        {
            UInt128 next = warp.Step(state, reached);
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
        if (state.HasPortals)
        {
            FillThroughPortals(state, player, distances);
            return;
        }

        distances.Fill(Unreachable);

        UInt128 reached = state.GoalMask(player);
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

    private static void FillThroughPortals(in GameState state, int player, Span<byte> distances)
    {
        distances.Fill(Unreachable);

        UInt128 reached = state.GoalMask(player);
        UInt128 previous = 0;
        byte distance = 0;
        var warp = new Warped(state);

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
            reached = warp.Step(state, reached);
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

            // A mouth's fifth neighbour. Without this the route stops dead at a portal it
            // was routed through, because no orthogonal neighbour is any closer — and the
            // wall search that reads the route would then only ever see its near half.
            if (state.IsPortalMouth(cell))
            {
                int partner = GameState.PortalPartner(cell);
                int d = distances[partner];
                if (d != Unreachable && d < bestDistance)
                {
                    bestDistance = d;
                    next = partner;
                }
            }

            if (next < 0) break;

            cell = next;
            cells.Add(cell);
        }
    }

    /// <summary>
    /// The pawn step that makes the most progress toward the player's goal, or null
    /// when the player is sealed off. Considers only what <c>GeneratePawnMoves</c> offers —
    /// steps, jumps and the portal step — so the result is always a legal move for the
    /// side to move.
    /// </summary>
    public static Move? BestStepTowardGoal(in GameState state, int player, Span<byte> distances)
    {
        Span<Move> buffer = stackalloc Move[10];
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
