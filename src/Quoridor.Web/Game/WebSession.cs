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

    public WebSession(
        WebMode mode,
        BotStrength strength,
        int localSeat = 0,
        GameSetup? setup = null,
        GameFlavour flavour = GameFlavour.Standard)
    {
        Mode = mode;
        Strength = strength;
        Setup = setup ?? GameSetup.Standard;
        Flavour = flavour;

        // Hotseat has no other side, so both seats are played from this keyboard.
        LocalSeat = mode == WebMode.Hotseat ? -1 : localSeat;

        // WebAssembly runs the search on the only thread there is, so the budget is
        // also how long the page stops answering. Kept short on purpose.
        _bot = mode == WebMode.VersusBot
            ? AgentFactory.Create(strength, TimeSpan.FromMilliseconds(600))
            : null;

        BuiltBoard built = Setup.Build();

        State = built.State;
        Holes = built.Holes;
    }

    public WebMode Mode { get; }

    public BotStrength Strength { get; }

    /// <summary>The board this game is played on, fixed once it has begun.</summary>
    public GameSetup Setup { get; }

    /// <summary>
    /// How the board was arrived at. Carried by the game rather than read off the setup
    /// screen, which may have been changed since — a rematch has to know whether it is
    /// repeating settings or rolling again.
    /// </summary>
    public GameFlavour Flavour { get; }

    /// <summary>The squares out of play, for the board to draw.</summary>
    public UInt128 Holes { get; }

    /// <summary>Which seat this browser plays, or -1 when it plays both.</summary>
    public int LocalSeat { get; }

    public GameState State { get; private set; }

    public IReadOnlyList<Move> Moves => _moves;

    public Move? LastMove => _moves.Count > 0 ? _moves[^1] : null;

    /// <summary>
    /// The position as it stood after the first <paramref name="plies"/> moves, for
    /// stepping back through a game that is still going on.
    /// </summary>
    public GameState StateAfter(int plies)
    {
        if (plies < 0) plies = 0;
        return plies < _positions.Count ? _positions[plies] : State;
    }

    public IQuoridorAgent? Bot => _bot;

    public int Winner => State.Winner;

    public bool IsOver => Winner >= 0;

    public bool IsBotTurn => !IsOver && _bot is not null && State.SideToMove != LocalSeat;

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

    /// <summary>Whether the move just played kept the turn, having found a free move.</summary>
    public bool LastMoveWentAgain { get; private set; }

    /// <summary>Whether the move just played picked a spare wall up off the board.</summary>
    public bool LastMoveTookAWall { get; private set; }

    /// <summary>Who made the move just played, or -1 for none — so what it did can be
    /// shown beside the player it happened to rather than to both of them.</summary>
    public int LastMover { get; private set; } = -1;

    public bool Apply(Move move)
    {
        if (IsOver || !State.IsLegal(move)) return false;

        _positions.Add(State);
        _moves.Add(move);

        int mover = State.SideToMove;
        int wallsBefore = State.WallsOf(mover);

        // Noted before the move, because taking it is what removes it.
        LastPickup = move.Kind == MoveKind.Pawn && (State.WallPickups & Board.Bit(move.Cell)) != 0
            ? (move.Cell, true)
            : move.Kind == MoveKind.Pawn && (State.SkipPickups & Board.Bit(move.Cell)) != 0
                ? (move.Cell, false)
                : null;

        GameState next = State;
        next.Apply(move);
        State = next;

        // Placing a wall spends one, so a supply that went up can only mean a pickup.
        LastMoveWentAgain = !IsOver && State.SideToMove == mover;
        LastMoveTookAWall = State.WallsOf(mover) > wallsBefore;
        LastMover = mover;

        return true;
    }

    /// <summary>The pickup the last move took and where, for the board to see it off.</summary>
    public (int Cell, bool IsWall)? LastPickup { get; private set; }

    public bool Undo()
    {
        if (!CanUndo) return false;

        int rewind = _bot is null ? 1 : 2;
        int target = _positions.Count - rewind;

        State = _positions[target];
        _positions.RemoveRange(target, rewind);
        _moves.RemoveRange(target, rewind);

        // What the last move did is no longer true of any move: the panel would go on
        // announcing a free move that has been taken back, and the board would see the
        // collected pickup off a second time as the position it was keyed on changed.
        LastMoveWentAgain = false;
        LastMoveTookAWall = false;
        LastPickup = null;
        LastMover = -1;

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
        if (Mode == WebMode.Hotseat) return $"Player {player + 1}";

        return player == LocalSeat ? "You" : _bot?.Name ?? "Opponent";
    }
}
