namespace Quoridor.Engine;

/// <summary>
/// Switches for the search techniques. Every one of them is a heuristic that trades
/// accuracy for depth, so every one of them can in principle make the engine worse.
/// Being able to turn them off individually is how you find out which.
/// </summary>
public sealed record EngineOptions(
    bool UseTranspositionTable = true,
    bool UseLateMoveReductions = true,
    bool UseAspirationWindows = true,
    bool UseHistoryOrdering = true,
    bool ScoreWallsEverywhere = false)
{
    public static readonly EngineOptions Default = new();
}
