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
            ? AgentFactory.Create(strength, TimeSpan.FromMilliseconds(FastBudgetMs))
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

    /// <summary>
    /// Whether the move just played went through a portal rather than across the board.
    /// The board draws that step as leave-and-arrive rather than a glide, and without
    /// saying so a pawn appearing half a board away reads as the game losing its place.
    /// </summary>
    public bool LastMoveCrossedPortal { get; private set; }

    /// <summary>
    /// Whether this move takes the mover from one mouth of a portal to the other. Asked
    /// of the position <em>before</em> the move: afterwards the pawn is at the far mouth
    /// and both ends look alike.
    /// </summary>
    private static bool CrossesPortal(in GameState state, Move move) =>
        move.Kind == MoveKind.Pawn &&
        state.HasPortals &&
        state.IsPortalMouth(state.PawnOf(state.SideToMove)) &&
        GameState.PortalPartner(state.PawnOf(state.SideToMove)) == move.Cell;

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

        // Also noted before the move, and for the same reason: the near mouth is only
        // identifiable while the pawn is still standing on it.
        LastMoveCrossedPortal = CrossesPortal(State, move);

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
        LastMoveCrossedPortal = false;
        LastPickup = null;
        LastMover = -1;

        return true;
    }

    // ================================================================== budget ==

    /// <summary>
    /// What the engine gets when the game is moving quickly, and what it has always
    /// had. On this build the budget is also how long the page stops answering, so it
    /// is the number the rest of the play loop is built around.
    /// </summary>
    public const int FastBudgetMs = 600;

    /// <summary>
    /// The most the engine may ever be given. The desktop can think while the player
    /// does; here there is one thread, so a longer search is a longer freeze and the
    /// ceiling is chosen for the freeze rather than for the strength: 900 ms more than
    /// usual, once, is a pause a person can place as the opponent thinking. Anything
    /// approaching two seconds reads as the page having stopped.
    /// </summary>
    public const int SlowestBudgetMs = 1500;

    /// <summary>
    /// How long the player is allowed to take before any of it comes back at them. Under
    /// this they are playing quickly, and a freeze that grows while the game is rattling
    /// along is felt as the page misbehaving rather than as an opponent thinking. It also
    /// makes a premove free: one is entered before the position exists, so it arrives with
    /// nothing on the clock and is answered at the usual speed.
    ///
    /// The first step the player can actually see is later than this, because the budget
    /// is rounded to a tenth of a second afterwards: it takes 3.1 s of thought to round up
    /// to 700 ms. Under that the answer is the usual 600 to the millisecond.
    /// </summary>
    private const int BriskMs = 2500;

    /// <summary>
    /// How much of the player's thinking the engine is given: one millisecond in twelve.
    /// Together with the ceiling and the rounding, the budget reaches its longest at
    /// 12.7 s of human thought, which is a genuinely long think.
    /// </summary>
    private const int Share = 12;

    /// <summary>
    /// How long the engine gets for its next move, given how long the player spent on
    /// theirs. Fast play is answered at exactly the speed it always was; a long think
    /// earns the engine a longer one, bounded so the page is never frozen for much more
    /// than a second.
    ///
    /// Rounded to a tenth of a second on purpose. A budget that lands on 1237 ms and then
    /// on 1244 ms wanders, and a wait that wanders reads as jitter; one that steps reads
    /// as a decision.
    /// </summary>
    public int BudgetFor(int humanMs)
    {
        // Easy and Normal answer from a heuristic in about a millisecond, so there is
        // nothing for a longer budget to be spent on and nothing to explain to the player.
        if (_bot is not SearchAgent) return FastBudgetMs;

        if (humanMs <= BriskMs) return FastBudgetMs;

        int budget = FastBudgetMs + ((humanMs - BriskMs) / Share);
        budget = Math.Clamp(budget, FastBudgetMs, SlowestBudgetMs);

        return (budget + 50) / 100 * 100;
    }

    /// <summary>
    /// Runs the engine for at most <paramref name="budgetMs"/>. There is no thread to move
    /// this off, so the caller has to let the browser paint before calling it — and has to
    /// have decided by then how long the page is going to be gone for.
    /// </summary>
    public Move Think(int budgetMs)
    {
        IQuoridorAgent bot = _bot ?? throw new InvalidOperationException("No engine in this game.");

        // The property is the one a chess clock uses on the desktop, and it means the same
        // thing here. Clamped rather than trusted: this is the only place that decides how
        // long the page may stop answering for, so it is the place to be sure of it.
        if (bot is SearchAgent engine)
            engine.MoveTime = TimeSpan.FromMilliseconds(Math.Clamp(budgetMs, FastBudgetMs, SlowestBudgetMs));

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
