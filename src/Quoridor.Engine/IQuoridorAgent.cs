using Quoridor.Core;

namespace Quoridor.Engine;

public enum BotStrength
{
    Easy,
    Normal,
    Hard,
}

/// <summary>
/// Anything that can pick a move for the side to move. The UI only ever talks to
/// this interface, so replacing the engine is a one-line change in the factory.
/// </summary>
public interface IQuoridorAgent
{
    string Name { get; }

    /// <summary>
    /// Returns a legal move for <c>state.SideToMove</c>. Implementations must honour
    /// <paramref name="cancellationToken"/> promptly and must never return an illegal
    /// move — the caller applies the result without re-validating.
    /// </summary>
    Move ChooseMove(in GameState state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Positions that already occurred in this game, oldest first, not including the
    /// one about to be searched. An engine uses this to recognise repetition and
    /// refuse to shuffle back and forth; simpler agents ignore it.
    /// </summary>
    void SetGameHistory(ReadOnlySpan<ulong> positionHashes)
    {
    }
}

public static class AgentFactory
{
    /// <summary>
    /// Builds the agent for a strength. <paramref name="ponder"/> asks the hard bot for
    /// an engine it can run while the opponent is on move; only a caller that will
    /// actually drive it should ask.
    /// </summary>
    public static IQuoridorAgent Create(
        BotStrength strength, TimeSpan? moveTime = null, int? seed = null, bool ponder = false) =>
        strength switch
        {
            BotStrength.Easy => new HeuristicAgent(BotStrength.Easy, seed),
            BotStrength.Normal => new HeuristicAgent(BotStrength.Normal, seed),

            // Single-threaded on purpose: measured over 20 games, eight threads score
            // 6:13 against one at the same clock. See the thread note in the README.
            _ => new SearchAgent(
                maxDepth: 32,
                moveTime: moveTime ?? TimeSpan.FromMilliseconds(1200),
                threads: 1,

                // The table stays 64 MB whether or not the bot ponders, and that is a
                // measured decision rather than an obvious one. The argument for growing
                // it was good: a ponder writes about a million entries a second for as
                // long as the human takes, so at 64 MB a long think spends its back half
                // evicting its own front half. The games disagreed. A 256 MB table with
                // pondering never switched on scored 39:56 over 96 games against the
                // ordinary one, losing or drawing all four independent runs — enough to
                // cancel the roughly +0.4 ply the ponder itself is worth. Whatever the
                // eviction costs, being four times the size costs more.
                tableMegabytes: 64,
                ponder: ponder),
        };
}
