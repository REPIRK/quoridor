using Quoridor.Core;
using Quoridor.Engine;

namespace Quoridor.App.Game;

public enum GameMode
{
    Hotseat,
    VersusBot,
    Spectate,
}

public sealed record GameOptions(
    GameMode Mode,
    BotStrength Strength,
    TimeControl Clock,
    bool HumanMovesFirst = true)
{
    public static GameOptions Hotseat(TimeControl clock) => new(GameMode.Hotseat, BotStrength.Normal, clock);

    public static GameOptions VersusBot(BotStrength strength, TimeControl clock, bool humanMovesFirst) =>
        new(GameMode.VersusBot, strength, clock, humanMovesFirst);

    public static GameOptions Spectate(BotStrength strength) =>
        new(GameMode.Spectate, strength, TimeControl.None);

    /// <summary>
    /// Which seat the local player occupies. Player 0 always moves first by the rules,
    /// so "you move second" means taking seat 1 — and the board gets turned around so
    /// that seat is still the near edge.
    /// </summary>
    public int HumanSeat => Mode == GameMode.VersusBot && !HumanMovesFirst ? 1 : 0;

    public string Title
    {
        get
        {
            string name = Mode switch
            {
                GameMode.Hotseat => "Local match",
                GameMode.Spectate => "Two engines",
                _ => $"Versus {Strength.ToString().ToLowerInvariant()} bot",
            };

            if (Mode == GameMode.VersusBot && !HumanMovesFirst) name += " · you move second";
            if (Clock.IsEnabled) name += $" · {Clock.Label}";

            return name;
        }
    }
}

/// <summary>
/// Owns the position, the move history, the clocks and whichever seats are played by
/// an engine. Knows nothing about WPF, so the rules and the presentation can be tested
/// and changed independently.
/// </summary>
public sealed class GameSession
{
    /// <summary>How much of the remaining budget the engine may spend on one move.</summary>
    private const int ClockShare = 20;

    private readonly List<GameState> _positions = new();
    private readonly List<Move> _moves = new();
    private readonly IQuoridorAgent?[] _agents = new IQuoridorAgent?[2];
    private readonly TimeSpan[] _remaining = new TimeSpan[2];

    private int _flagged = -1;

    public GameSession(GameOptions options)
    {
        Options = options;

        switch (options.Mode)
        {
            case GameMode.VersusBot:
                _agents[options.HumanSeat ^ 1] =
                    AgentFactory.Create(options.Strength, Settings.Current.EngineMoveTime);
                break;

            case GameMode.Spectate:
                // The full-strength engine on a shorter clock, so a watched game does
                // not take a quarter of an hour, against whatever you picked.
                _agents[0] = new SearchAgent(
                    maxDepth: 32, moveTime: TimeSpan.FromMilliseconds(600), threads: 1, tableMegabytes: 16);
                _agents[1] = AgentFactory.Create(options.Strength, Settings.Current.EngineMoveTime);
                break;
        }

        State = GameState.CreateInitial();
        ResetClocks();
    }

    public GameOptions Options { get; }

    public GameState State { get; private set; }

    public IReadOnlyList<Move> Moves => _moves;

    /// <summary>
    /// The position as it stood after the first <paramref name="plies"/> moves, for
    /// stepping back through a game that is still going on.
    /// </summary>
    public GameState StateAfter(int plies)
    {
        if (plies < 0) plies = 0;
        return plies < _positions.Count ? _positions[plies] : State;
    }

    public IQuoridorAgent? AgentOf(int player) => _agents[player];

    /// <summary>The engine playing against the local player, if there is exactly one.</summary>
    public IQuoridorAgent? Bot => Options.Mode == GameMode.VersusBot ? _agents[1] : null;

    public bool HasClock => Options.Clock.IsEnabled;

    public TimeSpan RemainingOf(int player) => _remaining[player];

    /// <summary>Set when a player ran out of time, otherwise -1.</summary>
    public int FlaggedPlayer => _flagged;

    public int Winner => _flagged >= 0 ? _flagged ^ 1 : State.Winner;

    public bool IsOver => Winner >= 0;

    public bool IsBotTurn => !IsOver && _agents[State.SideToMove] is not null;

    public bool IsHumanTurn => !IsOver && _agents[State.SideToMove] is null;

    /// <summary>In a bot game an undo rewinds a full round, so the human moves again.</summary>
    public bool CanUndo => Options.Mode switch
    {
        GameMode.Hotseat => _moves.Count > 0,
        GameMode.VersusBot => _moves.Count >= 2,
        _ => false,
    };

    public bool Apply(Move move)
    {
        if (IsOver || !State.IsLegal(move)) return false;

        int mover = State.SideToMove;

        _positions.Add(State);
        _moves.Add(move);

        GameState next = State;
        next.Apply(move);
        State = next;

        if (HasClock) _remaining[mover] += Options.Clock.Increment;

        return true;
    }

    /// <summary>
    /// Charges elapsed time to whoever is on move. Returns true when that just ran the
    /// clock out, so the caller can show the result.
    /// </summary>
    public bool ChargeClock(TimeSpan elapsed)
    {
        if (!HasClock || IsOver) return false;

        int side = State.SideToMove;
        _remaining[side] -= elapsed;

        if (_remaining[side] > TimeSpan.Zero) return false;

        _remaining[side] = TimeSpan.Zero;
        _flagged = side;
        return true;
    }

    public bool Undo()
    {
        if (!CanUndo) return false;

        int rewind = Options.Mode == GameMode.Hotseat ? 1 : 2;
        int target = _positions.Count - rewind;

        State = _positions[target];
        _positions.RemoveRange(target, rewind);
        _moves.RemoveRange(target, rewind);
        _flagged = -1;

        return true;
    }

    public void Restart()
    {
        _positions.Clear();
        _moves.Clear();
        _flagged = -1;
        State = GameState.CreateInitial();
        ResetClocks();
    }

    /// <summary>
    /// Runs the engine for the side to move, off the UI thread. When a clock is
    /// running the search budget is cut to a share of what is left, so the engine
    /// cannot think its way into losing on time.
    /// </summary>
    public Task<Move> ThinkAsync(CancellationToken cancellationToken)
    {
        IQuoridorAgent agent = _agents[State.SideToMove]
            ?? throw new InvalidOperationException("No engine plays this side.");

        if (HasClock && agent is SearchAgent engine)
        {
            TimeSpan share = _remaining[State.SideToMove] / ClockShare;
            engine.MoveTime = TimeSpan.FromMilliseconds(
                Math.Clamp(share.TotalMilliseconds, 60, engine.DefaultMoveTime.TotalMilliseconds));
        }

        GameState snapshot = State;

        var history = new ulong[_positions.Count];
        for (int i = 0; i < _positions.Count; i++) history[i] = _positions[i].Hash;
        agent.SetGameHistory(history);

        return Task.Run(() => agent.ChooseMove(snapshot, cancellationToken), cancellationToken);
    }

    /// <summary>Distance to goal for each player, for the side panels.</summary>
    public (int First, int Second) Distances()
    {
        GameState snapshot = State;
        return (PathFinder.Distance(snapshot, 0), PathFinder.Distance(snapshot, 1));
    }

    public string PlayerName(int player)
    {
        if (_agents[player] is { } agent)
            return Options.Mode == GameMode.Spectate && player == 0 ? "Engine" : agent.Name;

        return Options.Mode == GameMode.Hotseat ? $"Player {player + 1}" : "You";
    }

    private void ResetClocks()
    {
        _remaining[0] = Options.Clock.Initial;
        _remaining[1] = Options.Clock.Initial;
    }
}
