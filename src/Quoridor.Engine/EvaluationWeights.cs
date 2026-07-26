namespace Quoridor.Engine;

/// <summary>
/// Tunable weights for <see cref="Evaluation"/>. Kept as data rather than constants
/// so the benchmark can play weight sets against each other — which is the only
/// honest way to choose them.
/// </summary>
public sealed record EvaluationWeights(
    int Path,
    int Wall,
    int RaceVerdict,
    int WallUncertainty)
{
    /// <summary>One step of route. Everything else is priced against this.</summary>
    public const int Step = 100;

    public static readonly EvaluationWeights Default = new(
        Path: Step,

        // A wall in hand is future route: spent well it adds about two steps to the
        // opponent, so hoarding one is worth most of that. Pricing it low makes the
        // engine dump its whole supply early for a lead it cannot keep, then lose to
        // the walls the opponent still holds. Measured at equal depth over 16 games,
        // 180 beat 120 by 12:4 and 120 beat 60 by 11:5.
        Wall: 180,

        // Arriving first is the game, and it is a step function the route difference
        // alone cannot express: when both players simply walk, every step shortens
        // both routes and the difference never moves. But it is also a cliff, and a
        // deep search will happily chase a verdict flip that walls then overturn — so
        // it is priced as a strong hint rather than a conclusion. The two weights
        // interact: at Wall=120 the best verdict was 120 (11:5 over 340), but once
        // walls were priced properly 220 beat 120 by 18:6 over 24 games.
        RaceVerdict: 220,

        // Each wall still in play is one more thing that can overturn that verdict.
        WallUncertainty: 14);
}
