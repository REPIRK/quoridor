using Quoridor.Core;
using Quoridor.Engine;

namespace Quoridor.Web.Game;

public enum WebMode
{
    Hotseat,
    VersusBot,
    Online,
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

    public WebSession(WebMode mode, BotStrength strength, int localSeat = 0)
    {
        Mode = mode;
        Strength = strength;

        // Hotseat has no remote side, so both seats are played from this keyboard.
        LocalSeat = mode switch
        {
            WebMode.Hotseat => -1,
            WebMode.Online => localSeat,
            _ => 0,
        };

        // WebAssembly runs the search on the only thread there is, so the budget is
        // also how long the page stops answering. Kept short on purpose.
        _bot = mode == WebMode.VersusBot
            ? AgentFactory.Create(strength, TimeSpan.FromMilliseconds(600))
            : null;

        State = GameState.CreateInitial();
    }

    public WebMode Mode { get; }

    public BotStrength Strength { get; }

    /// <summary>Which seat this browser plays, or -1 when it plays both.</summary>
    public int LocalSeat { get; }

    public GameState State { get; private set; }

    public IReadOnlyList<Move> Moves => _moves;

    public Move? LastMove => _moves.Count > 0 ? _moves[^1] : null;

    public IQuoridorAgent? Bot => _bot;

    public int Winner => State.Winner;

    public bool IsOver => Winner >= 0;

    public bool IsBotTurn => !IsOver && _bot is not null && State.SideToMove == 1;

    /// <summary>Whether the person at this keyboard may move right now.</summary>
    public bool IsHumanTurn =>
        !IsOver && !IsBotTurn && (LocalSeat < 0 || State.SideToMove == LocalSeat);

    // Taking a move back needs the other player's agreement, which there is no way to
    // ask for — so online games do not offer it.
    public bool CanUndo => Mode switch
    {
        WebMode.Online => false,
        WebMode.Hotseat => _moves.Count > 0,
        _ => _moves.Count >= 2,
    };

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
        if (Mode == WebMode.Online) return player == LocalSeat ? "You" : "Opponent";
        if (_bot is null) return $"Player {player + 1}";

        return player == 0 ? "You" : _bot.Name;
    }
}
