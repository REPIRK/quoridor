using Quoridor.Core;
using Quoridor.Engine;

namespace Quoridor.Web.Game;

public enum WebMode
{
    Hotseat,
    VersusBot,
}

/// <summary>
/// The browser build's game state. Deliberately thinner than the desktop session —
/// no clocks, no spectating, no review — because the point of this build is that
/// someone can try the engine in one click, not that it matches the desktop app
/// feature for feature.
///
/// The engine itself is the same assembly, unchanged.
/// </summary>
public sealed class WebSession
{
    private readonly List<GameState> _positions = new();
    private readonly List<Move> _moves = new();
    private readonly IQuoridorAgent? _bot;

    public WebSession(WebMode mode, BotStrength strength)
    {
        Mode = mode;
        Strength = strength;

        // WebAssembly runs the search on the only thread there is, so the budget is
        // also how long the page stops answering. Kept short on purpose.
        _bot = mode == WebMode.VersusBot
            ? AgentFactory.Create(strength, TimeSpan.FromMilliseconds(600))
            : null;

        State = GameState.CreateInitial();
    }

    public WebMode Mode { get; }

    public BotStrength Strength { get; }

    public GameState State { get; private set; }

    public IReadOnlyList<Move> Moves => _moves;

    public Move? LastMove => _moves.Count > 0 ? _moves[^1] : null;

    public IQuoridorAgent? Bot => _bot;

    public int Winner => State.Winner;

    public bool IsOver => Winner >= 0;

    public bool IsBotTurn => !IsOver && _bot is not null && State.SideToMove == 1;

    public bool IsHumanTurn => !IsOver && !IsBotTurn;

    public bool CanUndo => _bot is null ? _moves.Count > 0 : _moves.Count >= 2;

    public bool Apply(Move move)
    {
        if (IsOver || !State.IsLegal(move)) return false;

        _positions.Add(State);
        _moves.Add(move);

        GameState next = State;
        next.Apply(move);
        State = next;

        return true;
    }

    public bool Undo()
    {
        if (!CanUndo) return false;

        int rewind = _bot is null ? 1 : 2;
        int target = _positions.Count - rewind;

        State = _positions[target];
        _positions.RemoveRange(target, rewind);
        _moves.RemoveRange(target, rewind);

        return true;
    }

    /// <summary>
    /// Runs the engine. There is no thread to move this off, so the caller has to let
    /// the browser paint before calling it.
    /// </summary>
    public Move Think()
    {
        IQuoridorAgent bot = _bot ?? throw new InvalidOperationException("No engine in this game.");

        var history = new ulong[_positions.Count];
        for (int i = 0; i < _positions.Count; i++) history[i] = _positions[i].Hash;
        bot.SetGameHistory(history);

        return bot.ChooseMove(State);
    }

    public (int First, int Second) Distances()
    {
        GameState snapshot = State;
        return (PathFinder.Distance(snapshot, 0), PathFinder.Distance(snapshot, 1));
    }

    public string PlayerName(int player)
    {
        if (_bot is null) return $"Player {player + 1}";
        return player == 0 ? "You" : _bot.Name;
    }
}
