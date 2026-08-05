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
    /// Last resort: any legal pawn step. A Quoridor position always has one, since
    /// walls may never remove every route.
    /// </summary>
    internal static Move Fallback(in GameState state)
    {
        Span<Move> buffer = stackalloc Move[10];
        int count = state.GeneratePawnMoves(buffer);
        if (count > 0) return buffer[0];

        throw new InvalidOperationException("Position has no legal pawn move, which the rules make impossible.");
    }
}
