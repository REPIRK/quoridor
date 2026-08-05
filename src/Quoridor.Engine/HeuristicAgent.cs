using Quoridor.Core;

namespace Quoridor.Engine;

/// <summary>
/// A one-ply bot. Easy walks its shortest route and only occasionally reaches for a
/// wall; Normal scores every candidate move with the static evaluation and takes the
/// best, with a little noise so repeated games do not play out identically.
/// </summary>
public sealed class HeuristicAgent : IQuoridorAgent
{
    private readonly BotStrength _strength;
    private readonly Random _random;

    public HeuristicAgent(BotStrength strength, int? seed = null)
    {
        _strength = strength;
        _random = seed is null ? new Random() : new Random(seed.Value);
    }

    public string Name => _strength == BotStrength.Easy ? "Bot · Easy" : "Bot · Normal";

    public Move ChooseMove(in GameState state, CancellationToken cancellationToken = default)
    {
        return _strength == BotStrength.Easy ? ChooseEasy(state) : ChooseNormal(state, cancellationToken);
    }

    private Move ChooseEasy(in GameState state)
    {
        int me = state.SideToMove;
        int opponent = me ^ 1;

        bool losingTheRace = PathFinder.Distance(state, opponent) < PathFinder.Distance(state, me);
        bool feelsLikeWalling = state.WallsOf(me) > 0 && losingTheRace && _random.NextDouble() < 0.35;

        if (feelsLikeWalling)
        {
            Span<Move> candidates = stackalloc Move[MoveCandidates.MaxMoves];
            int count = MoveCandidates.Generate(state, candidates, maxWalls: 6, scoreWalls: true);

            int firstWall = -1;
            int wallCount = 0;
            for (int i = 0; i < count; i++)
            {
                if (!candidates[i].IsWall) continue;
                if (firstWall < 0) firstWall = i;
                wallCount++;
            }

            if (wallCount > 0) return candidates[firstWall + _random.Next(wallCount)];
        }

        Span<byte> distances = stackalloc byte[Board.CellCount];
        PathFinder.FillDistancesToGoal(state, me, distances);

        Move? step = PathFinder.BestStepTowardGoal(state, me, distances);
        return step ?? Fallback(state);
    }

    private Move ChooseNormal(in GameState state, CancellationToken cancellationToken)
    {
        int me = state.SideToMove;

        Span<Move> candidates = stackalloc Move[MoveCandidates.MaxMoves];
        int count = MoveCandidates.Generate(state, candidates, maxWalls: 20, scoreWalls: true);

        if (count == 0) return Fallback(state);

        Move best = candidates[0];
        int bestScore = int.MinValue;

        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            GameState next = state;
            next.Apply(candidates[i]);

            int score = Evaluation.Evaluate(next, me) + _random.Next(-4, 5);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidates[i];
            }
        }

        return best;
    }

    /// <summary>
    /// Last resort: any legal move at all. The pawn steps are asked for first because
    /// they are one generation rather than eighty-odd wall placements, not because they
    /// are the only kind that counts.
    ///
    /// It used to stop there and throw, on the argument that walls may never remove every
    /// route so a pawn always has a step. Portals broke that: a pawn can be boxed in with
    /// its escape route running through the opponent's portal mouth, and having no step is
    /// then a perfectly ordinary position that the player answers with a wall. The rules
    /// forfeit a turn only when there is no move of either kind
    /// (<c>GameState.Apply</c>), so reaching the throw below means a position was built
    /// past the rules rather than played into — which is worth saying out loud, since
    /// every agent funnels through here.
    /// </summary>
    internal static Move Fallback(in GameState state)
    {
        Span<Move> buffer = stackalloc Move[10];
        if (state.GeneratePawnMoves(buffer) > 0) return buffer[0];

        List<Move> everything = state.LegalMoves();
        if (everything.Count > 0) return everything[0];

        throw new InvalidOperationException(
            "Position has no legal move of any kind, which GameState.Apply forfeits the turn to avoid.");
    }
}
