using Quoridor.Core;

namespace Quoridor.Engine;

/// <summary>
/// Static evaluation, always from the point of view of <c>player</c>.
///
/// The obvious term is the difference in shortest-path lengths, but on its own it is
/// badly misleading: when both players simply walk, every step shortens both routes
/// and the difference never moves. A dead-even race and a lost race score the same,
/// so an engine using only that term cannot tell that racing wins and goes looking
/// for something to do with its walls instead — which is exactly how it throws a won
/// game away.
///
/// So the race verdict is scored explicitly. Who arrives first is a step function,
/// not a slope, and its weight depends on how many walls are still in hand: with none
/// left the verdict is the whole game, with twenty it is barely a hint.
/// </summary>
public static class Evaluation
{
    public const int Mate = 1_000_000;

    /// <summary>Scores above this magnitude are forced results, not estimates.</summary>
    public const int MateThreshold = 900_000;

    /// <summary>Returned by <see cref="RaceScore"/> when the race is too close to call.</summary>
    public const int Unknown = int.MinValue;

    public static int Evaluate(in GameState state, int player) =>
        Evaluate(state, player, EvaluationWeights.Default);

    public static int Evaluate(in GameState state, int player, EvaluationWeights weights)
    {
        int opponent = player ^ 1;

        int myDistance = PathFinder.Distance(state, player);
        int theirDistance = PathFinder.Distance(state, opponent);

        // A sealed-off player can only happen through a bug; score it as a loss for
        // whoever is stuck rather than propagating a nonsense number up the tree.
        if (myDistance < 0) return -Mate;
        if (theirDistance < 0) return Mate;

        if (myDistance == 0) return Mate;
        if (theirDistance == 0) return -Mate;

        int score = (theirDistance - myDistance) * weights.Path;
        score += (state.WallsOf(player) - state.WallsOf(opponent)) * weights.Wall;

        int wallsInPlay = state.WallsOf(0) + state.WallsOf(1);
        int verdict = Math.Max(0, weights.RaceVerdict - wallsInPlay * weights.WallUncertainty);

        if (verdict > 0)
            score += ArrivesFirst(state, player, myDistance, theirDistance) ? verdict : -verdict;

        if (weights.Pickup > 0 && state.HasPickups)
            score += PickupEdge(state, player, weights);

        return score;
    }

    /// <summary>
    /// What the pickups still on the board are worth to each side, by how near they are.
    ///
    /// Without this the engine only ever finds one by searching onto it, so anything
    /// more than a few plies away is invisible and it walks past prizes a person plans
    /// a route around. Nearness is measured as plain distance ignoring walls: a real
    /// one would want a flood fill from each pawn, which would double what an
    /// evaluation costs, and the point here is only to lean the search the right way —
    /// the search itself works out whether the trip is actually possible.
    /// </summary>
    private static int PickupEdge(in GameState state, int player, EvaluationWeights weights)
    {
        // How far a pickup can be and still pull at all. Beyond this the trip costs
        // more than anything on the board is worth.
        const int Reach = 5;

        int total = 0;

        for (int kind = 0; kind < 2; kind++)
        {
            UInt128 remaining = kind == 0 ? state.WallPickups : state.SkipPickups;
            if (remaining == 0) continue;

            // A spare wall is two walls; a free move is a whole move, which is worth
            // rather more than the step it buys because the other player loses theirs.
            int worth = kind == 0
                ? weights.Wall * GameState.WallsPerPickup
                : weights.Path * 2;

            while (remaining != 0)
            {
                int cell = Board.LowestBit(remaining);
                remaining &= remaining - UInt128.One;

                total += Pull(state, player, cell, worth, weights) -
                         Pull(state, player ^ 1, cell, worth, weights);
            }
        }

        return total;

        static int Pull(in GameState state, int who, int cell, int worth, EvaluationWeights weights)
        {
            int pawn = state.PawnOf(who);

            int steps = Math.Abs(Board.RowOf(pawn) - Board.RowOf(cell)) +
                        Math.Abs(Board.ColOf(pawn) - Board.ColOf(cell));

            if (steps >= Reach) return 0;

            return worth * weights.Pickup * (Reach - steps) / (Reach * 100);
        }
    }

    /// <summary>
    /// Whether <paramref name="player"/> reaches their goal row first if both players
    /// just walk. The side to move gets there on their own move number <c>d</c>, the
    /// other side one half-move later, so ties go to whoever is on move.
    /// </summary>
    private static bool ArrivesFirst(in GameState state, int player, int myDistance, int theirDistance)
    {
        bool playerIsOnMove = state.SideToMove == player;

        return playerIsOnMove
            ? myDistance <= theirDistance
            : myDistance < theirDistance;
    }

    /// <summary>
    /// Exact result for a position where neither player has walls left: the game has
    /// become a pure race and nothing either player does can change the outcome.
    ///
    /// The one wrinkle is jumping, which can shorten a route by a single move. Taking
    /// that slack into account, the verdict is certain unless the two routes are
    /// exactly equal, which falls through to normal search.
    /// </summary>
    public static int RaceScore(in GameState state, int ply)
    {
        int mover = state.SideToMove;

        int mine = PathFinder.Distance(state, mover);
        int theirs = PathFinder.Distance(state, mover ^ 1);

        if (mine < 0 || theirs < 0) return Unknown;

        // Worst case for the mover is the opponent finding a jump and the mover not.
        if (mine <= theirs - 1) return Mate - ply - mine;

        // Best case for the mover is the mirror of that.
        if (mine >= theirs + 2) return -(Mate - ply - theirs);

        return Unknown;
    }

    /// <summary>Distance to goal for both players, from <paramref name="player"/>'s side.</summary>
    public static (int Mine, int Theirs) Distances(in GameState state, int player) =>
        (PathFinder.Distance(state, player), PathFinder.Distance(state, player ^ 1));
}
