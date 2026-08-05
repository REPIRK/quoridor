using Quoridor.Core;
using Quoridor.Engine;

namespace Quoridor.Session;

public enum GameMode
{
    Hotseat,
    VersusBot,
    Spectate,
    Online,
}

public sealed record GameOptions(
    GameMode Mode,
    BotStrength Strength,
    TimeControl Clock,
    bool HumanMovesFirst = true,
    GameSetup? Board = null,
    GameFlavour Flavour = GameFlavour.Standard)
{
    /// <summary>The board this game is played on, standard unless something says otherwise.</summary>
    public GameSetup Setup => Board ?? GameSetup.Standard;

    public static GameOptions Hotseat(TimeControl clock, GameSetup board, GameFlavour flavour) =>
        new(GameMode.Hotseat, BotStrength.Normal, clock, Board: board, Flavour: flavour);

    public static GameOptions VersusBot(
        BotStrength strength, TimeControl clock, bool humanMovesFirst, GameSetup board, GameFlavour flavour) =>
        new(GameMode.VersusBot, strength, clock, humanMovesFirst, board, flavour);

    public static GameOptions Spectate(BotStrength strength, GameSetup board, GameFlavour flavour) =>
        new(GameMode.Spectate, strength, TimeControl.None, Board: board, Flavour: flavour);

    /// <summary>A network game is set up by the host, who says which seat and board.</summary>
    public static GameOptions Online(bool isHost, GameSetup board) =>
        new(GameMode.Online, BotStrength.Normal, TimeControl.None, HumanMovesFirst: isHost, Board: board);

    /// <summary>
    /// Which seat the local player occupies. Player 0 always moves first by the rules,
    /// so "you move second" means taking seat 1 — and the board gets turned around so
    /// that seat is still the near edge.
    /// </summary>
    public int HumanSeat => !HumanMovesFirst && Mode is GameMode.VersusBot or GameMode.Online ? 1 : 0;

    public string Title => TitleFor(Setup);

    /// <summary>
    /// The header for a game played on <paramref name="board"/>. Takes the board rather
    /// than reading its own, because a rolled game rolls again on a rematch and the two
    /// part company from then on.
    /// </summary>
    public string TitleFor(GameSetup board)
    {
        string name = Mode switch
        {
            GameMode.Hotseat => "Local match",
            GameMode.Spectate => "Two engines",
            GameMode.Online => "Network game",
            _ => $"Versus {Strength.ToString().ToLowerInvariant()} bot",
        };

        if (!board.IsStandard) name += $" · {board.Describe()}";
        if (Mode == GameMode.VersusBot && !HumanMovesFirst) name += " · you move second";
        if (Clock.IsEnabled) name += $" · {Clock.Label}";

        return name;
    }
}

/// <summary>
/// The two engine preferences a game cannot decide for itself: how long the engine may
/// think about one move, and whether it may also think while the human is deciding.
/// </summary>
public sealed record EnginePreferences(TimeSpan MoveTime, bool Ponder)
{
    /// <summary>
    /// What a host that has said nothing gets. Read off an untouched <see cref="Settings"/>
    /// rather than written out a second time, so the numbers have one home.
    /// </summary>
    public static readonly EnginePreferences Defaults = Of(new Settings());

    /// <summary>The two values above as a settings object has them.</summary>
    public static EnginePreferences Of(Settings settings) => new(settings.EngineMoveTime, settings.Ponder);
}

/// <summary>
/// Owns the position, the move history, the clocks and whichever seats are played by
/// an engine. Knows nothing about WPF, so the rules and the presentation can be tested
/// and changed independently.
/// </summary>
public sealed class GameSession
{
    /// <summary>
    /// Where a session gets its engine preferences. It is handed them rather than going
    /// and finding them, because where they are kept is the host's business — a file in a
    /// roaming profile on Windows, and something else on a phone.
    ///
    /// A function rather than a value, because it is asked afresh every time it is needed:
    /// turning pondering off in the settings screen has always taken effect on the game
    /// already in progress, and a snapshot taken when the game began would not.
    /// </summary>
    public static Func<EnginePreferences> Preferences { get; set; } = () => EnginePreferences.Defaults;

    /// <summary>How much of the remaining budget the engine may spend on one move.</summary>
    private const int ClockShare = 20;

    private readonly List<GameState> _positions = new();
    private readonly List<Move> _moves = new();
    private readonly IQuoridorAgent?[] _agents = new IQuoridorAgent?[2];
    private readonly TimeSpan[] _remaining = new TimeSpan[2];

    private int _flagged = -1;

    /// <summary>
    /// The tail of the chain of engine searches: completed when no agent is thinking.
    /// See <see cref="ThinkAsync"/> for why the searches have to be a chain and not a
    /// crowd.
    /// </summary>
    private Task _agentIdle = Task.CompletedTask;

    /// <summary>
    /// Stops the search the engine is running on the human's time. It is a second token
    /// and not the one above because a ponder runs on its own engine, so the two are
    /// never the same search and are never cancelled for the same reason: this one ends
    /// because the position moved on, that one because the answer is no longer wanted.
    /// </summary>
    private CancellationTokenSource? _ponderStop;

    /// <summary>
    /// The tail of the chain of ponders, completed when the ponder engine is free. One
    /// engine means one search at a time here as much as it does above.
    /// </summary>
    private Task _ponderIdle = Task.CompletedTask;

    public GameSession(GameOptions options)
    {
        Options = options;

        EnginePreferences engine = Preferences();

        switch (options.Mode)
        {
            case GameMode.VersusBot:
                _agents[options.HumanSeat ^ 1] = AgentFactory.Create(
                    options.Strength, engine.MoveTime, ponder: engine.Ponder);
                break;

            case GameMode.Spectate:
                // The full-strength engine on a shorter clock, so a watched game does
                // not take a quarter of an hour, against whatever you picked.
                _agents[0] = new SearchAgent(
                    maxDepth: 32, moveTime: TimeSpan.FromMilliseconds(600), threads: 1, tableMegabytes: 16);
                _agents[1] = AgentFactory.Create(options.Strength, engine.MoveTime);
                break;
        }

        Setup = options.Setup;

        BuiltBoard built = Setup.Build();

        State = built.State;
        Holes = built.Holes;

        ResetClocks();
    }

    public GameOptions Options { get; }

    /// <summary>
    /// The board actually in play. Normally the one the menu chose, but a rolled game
    /// rolls again on a rematch, so this and <c>Options.Setup</c> part company the first
    /// time Restart is pressed.
    /// </summary>
    public GameSetup Setup { get; private set; }

    /// <summary>The squares out of play, for the board to draw.</summary>
    public UInt128 Holes { get; private set; }

    public GameState State { get; private set; }

    public IReadOnlyList<Move> Moves => _moves;

    /// <summary>The move just played, or none in a game nothing has been played in yet.</summary>
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

    /// <summary>
    /// Whether the person at this keyboard may move. In a network game only one seat is
    /// theirs; in a local match both are.
    /// </summary>
    public bool IsHumanTurn => !IsOver && IsHumanSeat(State.SideToMove);

    /// <summary>
    /// The seat the person at this device plays, or -1 when they play both of them or
    /// neither. It is not the same question as <see cref="GameOptions.HumanSeat"/>, which
    /// answers "which end of the board is ours" for every mode and so says 0 for a local
    /// match and for a watched game as well — true of the drawing, and wrong for anything
    /// that has to know whose result this is.
    /// </summary>
    public int LocalSeat => Options.Mode is GameMode.Hotseat or GameMode.Spectate ? -1 : Options.HumanSeat;

    /// <summary>Whether the person at this keyboard is the one who plays this seat.</summary>
    private bool IsHumanSeat(int seat) => _agents[seat] is null &&
        (Options.Mode != GameMode.Online || seat == Options.HumanSeat);

    /// <summary>
    /// An undo hands the board back to the player, so it is offered exactly when there is
    /// a turn of theirs to go back to. Over the network it would need the other player's
    /// agreement, which there is no way to ask for, so it is not offered at all.
    /// </summary>
    public bool CanUndo => PreviousHumanTurn() >= 0;

    /// <summary>Whether the move just played kept the turn, having found a free move.</summary>
    public bool LastMoveWentAgain { get; private set; }

    /// <summary>
    /// Whether the turn came back because the other player had nothing legal to play and
    /// forfeited it (<c>GameState.Apply</c>). It looks exactly like a free move from here
    /// — the same player is on move twice running — and it is not one: nothing was picked
    /// up, and on a board carrying no skip pickups at all there is nothing it could have
    /// been picked up from. Told apart by what the move landed on, because a free move
    /// only ever comes off a skip square.
    /// </summary>
    public bool LastTurnForfeited { get; private set; }

    /// <summary>Whether the move just played picked a spare wall up off the board.</summary>
    public bool LastMoveTookAWall { get; private set; }

    /// <summary>
    /// Whether the move just played went through a portal rather than across the board.
    /// Without saying so, a pawn arriving half a board away reads as the game losing its
    /// place rather than as the move it was.
    /// </summary>
    public bool LastMoveCrossedPortal { get; private set; }

    /// <summary>
    /// The pickup the move just played took and the square it was standing on, so the
    /// board can see it off from where it was collected rather than have it vanish.
    /// </summary>
    public (int Cell, bool IsWall)? LastPickup { get; private set; }

    /// <summary>
    /// Who made the move just played, or -1 for none. What a move did is shown beside the
    /// player it happened to, and on a board with skip pickups the turn does not alternate,
    /// so the side to move is not the side that just moved.
    /// </summary>
    public int LastMover { get; private set; } = -1;

    public bool Apply(Move move)
    {
        if (IsOver || !State.IsLegal(move)) return false;

        StopPondering();

        int mover = State.SideToMove;
        int wallsBefore = State.WallsOf(mover);

        // Asked before the move: afterwards the pawn is at the far mouth and the two
        // ends of a portal look exactly alike.
        bool crossedPortal = move.Kind == MoveKind.Pawn &&
            State.HasPortals &&
            State.IsPortalMouth(State.PawnOf(mover)) &&
            GameState.PortalPartner(State.PawnOf(mover)) == move.Cell;

        // Also asked before the move, and for the same reason: stepping on a skip pickup
        // is what takes it off the board. This is the whole difference between the two
        // ways the turn can come straight back — a free move was taken here, or the other
        // player had nothing to play and forfeited theirs.
        bool tookFreeMove = move.Kind == MoveKind.Pawn && (State.SkipPickups & Board.Bit(move.Cell)) != 0;

        // And once more for the same reason: what was standing on the square is only
        // readable while it is still standing there.
        LastPickup = move.Kind == MoveKind.Pawn && (State.WallPickups & Board.Bit(move.Cell)) != 0
            ? (move.Cell, true)
            : tookFreeMove
                ? (move.Cell, false)
                : null;

        _positions.Add(State);
        _moves.Add(move);

        GameState next = State;
        next.Apply(move);
        State = next;

        // Placing a wall spends one, so a supply that went up can only mean a pickup.
        bool cameStraightBack = !IsOver && State.SideToMove == mover;

        LastMoveWentAgain = cameStraightBack && tookFreeMove;
        LastTurnForfeited = cameStraightBack && !tookFreeMove;
        LastMoveTookAWall = State.WallsOf(mover) > wallsBefore;
        LastMoveCrossedPortal = crossedPortal;
        LastMover = mover;

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

    /// <summary>
    /// The ply the board should fall back to when the player asks to take a move back:
    /// the last position it was their turn in. -1 when there is none, which is what makes
    /// taking a move back unavailable.
    ///
    /// Searched for rather than counted. A fixed rewind of two plies assumes the turn
    /// alternates, and it does not: a skip pickup gives a free move that keeps the turn,
    /// so on a board carrying one the count lands on the wrong seat. It is wrong again at
    /// the end of every game the human won, where the last ply is theirs and two plies
    /// back is the engine's turn — which is how undoing a won game used to leave the
    /// engine on move with nobody to run it.
    /// </summary>
    private int PreviousHumanTurn()
    {
        // Both seats are the player's in a local match, one is theirs against the engine,
        // and neither is in a watched game. A network undo would need the other player's
        // agreement, which there is no way to ask for.
        if (Options.Mode is not (GameMode.Hotseat or GameMode.VersusBot)) return -1;

        for (int ply = _positions.Count - 1; ply >= 0; ply--)
            if (IsHumanSeat(_positions[ply].SideToMove)) return ply;

        return -1;
    }

    public bool Undo()
    {
        int target = PreviousHumanTurn();
        if (target < 0) return false;

        StopPondering();

        State = _positions[target];
        _positions.RemoveRange(target, _positions.Count - target);
        _moves.RemoveRange(target, _moves.Count - target);
        _flagged = -1;

        ForgetLastMove();
        PositionChanged?.Invoke();

        return true;
    }

    public void Restart()
    {
        StopPondering();

        _positions.Clear();
        _moves.Clear();
        _flagged = -1;

        // A rolled game rolls again. Playing the same random board twice is not what
        // "random" was asked for, and the browser build has always worked this way — the
        // desktop simply never carried the flavour far enough to know. Standard and
        // Custom are settings rather than a throw of the dice, so they stand.
        if (Options.Flavour == GameFlavour.Random) Setup = GameSetup.Roll(Random.Shared.Next());

        BuiltBoard built = Setup.Build();

        State = built.State;
        Holes = built.Holes;

        ResetClocks();

        ForgetLastMove();
        PositionChanged?.Invoke();
    }

    /// <summary>
    /// Raised when the session has moved the game somewhere nobody played to — an undo or
    /// a restart. Whoever is on move afterwards is not who was on move before, and if that
    /// is an engine then something has to set it going: this says so once, here, rather
    /// than leaving every caller to remember. Undo was the one that did not.
    ///
    /// <see cref="Apply"/> deliberately does not raise it. The view is mid-animation there
    /// and owns what happens next.
    /// </summary>
    public event Action? PositionChanged;

    /// <summary>
    /// Drops what the last move did. Everything below describes the move just played, and
    /// after a rewind or a restart no move was just played — without this the panel went on
    /// announcing the pickup or the portal from the game that has been taken off the board,
    /// and the board saw a collected pickup off a second time as the position it was keyed
    /// on changed under it.
    /// </summary>
    private void ForgetLastMove()
    {
        LastMoveWentAgain = false;
        LastTurnForfeited = false;
        LastMoveTookAWall = false;
        LastMoveCrossedPortal = false;
        LastPickup = null;
        LastMover = -1;
    }

    /// <summary>
    /// Runs the engine for the side to move, off the UI thread. When a clock is
    /// running the search budget is cut to a share of what is left, so the engine
    /// cannot think its way into losing on time.
    ///
    /// An agent keeps the whole of its search state in instance fields — move stack,
    /// killers, history, and the flag that says to stop — so two searches running on one
    /// at the same time would overwrite each other's working buffers, and starting the
    /// second would clear the stop flag the first is watching and so un-cancel the very
    /// search that was being abandoned. Cancelling is therefore a request and not an
    /// event: each search waits for the one before it to put the agent down before it
    /// touches the agent at all.
    /// </summary>
    public Task<Move> ThinkAsync(CancellationToken cancellationToken)
    {
        // The chain is extended before the caller can await it, so a second call made
        // while this one is still queued lines up behind it rather than beside it.
        Task<Move> search = ThinkAfterAsync(_agentIdle, cancellationToken);
        _agentIdle = search;
        return search;
    }

    private async Task<Move> ThinkAfterAsync(Task previous, CancellationToken cancellationToken)
    {
        if (!previous.IsCompleted)
        {
            // How the search before this one ended is its own caller's business; all
            // this one needs is for it to be over.
            try
            {
                await previous;
            }
            catch
            {
            }
        }

        // The ponder is on its own engine and the table is lock-free, so this is not a
        // matter of correctness — it is a matter of speed. Pondering is only free if the
        // engine's answer comes back no slower than it does without it, and that is only
        // true if the ponder is off the processor before the real search is on it.
        StopPondering();

        if (!_ponderIdle.IsCompleted)
        {
            try
            {
                await _ponderIdle;
            }
            catch
            {
            }
        }

        // Waiting for the agent can take as long as the abandoned search does, by which
        // time this one may have been given up on too.
        cancellationToken.ThrowIfCancellationRequested();

        IQuoridorAgent agent = _agents[State.SideToMove]
            ?? throw new InvalidOperationException("No engine plays this side.");

        if (HasClock && agent is SearchAgent engine)
        {
            TimeSpan share = _remaining[State.SideToMove] / ClockShare;
            engine.MoveTime = TimeSpan.FromMilliseconds(
                Math.Clamp(share.TotalMilliseconds, 60, engine.DefaultMoveTime.TotalMilliseconds));
        }

        GameState snapshot = State;

        agent.SetGameHistory(HistoryHashes());

        return await Task.Run(() => agent.ChooseMove(snapshot, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Sets the engine searching while the human is looking at the board, on the thread it
    /// would otherwise spend idle. Nothing is played out of it and no answer is kept —
    /// the one thing it leaves behind is a warmed transposition table. So the engine's own
    /// answer arrives exactly as fast as it does today, and rather better informed.
    ///
    /// What is handed over is the position the human is deciding from. Which position the
    /// engine then searches is its own business: it searches the one after the reply it
    /// expects, so on the roughly half of moves where the guess is right the game walks
    /// into exactly the position that was analysed, and on the rest the work is wasted
    /// rather than harmful. Measured at nearly twice the depth gain of searching the
    /// handed-over position itself, which is what this used to do.
    ///
    /// Only against the engine, and only while a human really is on move. A local match
    /// and a network game have no engine to spare, and a watched game has no idle thread
    /// to fill because the engine is the one thinking in it.
    /// </summary>
    public void StartPondering()
    {
        if (!Preferences().Ponder || Options.Mode != GameMode.VersusBot || !IsHumanTurn) return;
        if (_agents[State.SideToMove ^ 1] is not SearchAgent engine || !engine.CanPonder) return;

        StopPondering();

        // Cancelled and dropped rather than disposed: the ponder being cancelled goes on
        // reading the token it was handed until it notices, and a source with no
        // registrations and no wait handle has nothing to release.
        var stop = new CancellationTokenSource();
        _ponderStop = stop;

        CancellationToken token = stop.Token;
        GameState snapshot = State;
        ulong[] history = HistoryHashes();

        // Chained behind the previous ponder for the same reason the real searches are
        // chained: one engine, one search at a time. The one before this has already
        // been asked to stop, so the wait is milliseconds.
        Task previous = _ponderIdle;

        _ponderIdle = Task.Run(async () =>
        {
            if (!previous.IsCompleted)
            {
                try
                {
                    await previous;
                }
                catch
                {
                }
            }

            engine.Ponder(snapshot, history, token);
        });
    }

    /// <summary>
    /// Asks the ponder to stop, without waiting for it to notice. Every method that
    /// moves the position on calls this, which is what stops a ponder outliving the
    /// position it was rooted at: the view has only to decide when to start one, and
    /// cannot forget to end it.
    /// </summary>
    public void StopPondering() => _ponderStop?.Cancel();

    /// <summary>The hash of every position the game has already stood in, oldest first.</summary>
    private ulong[] HistoryHashes()
    {
        var history = new ulong[_positions.Count];
        for (int i = 0; i < _positions.Count; i++) history[i] = _positions[i].Hash;
        return history;
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

        if (Options.Mode == GameMode.Online)
            return player == Options.HumanSeat ? "You" : "Opponent";

        return Options.Mode == GameMode.Hotseat ? $"Player {player + 1}" : "You";
    }

    private void ResetClocks()
    {
        _remaining[0] = Options.Clock.Initial;
        _remaining[1] = Options.Clock.Initial;
    }
}
