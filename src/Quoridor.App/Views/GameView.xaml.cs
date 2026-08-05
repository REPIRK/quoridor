using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Quoridor.App.Controls;
using Quoridor.App.Game;
using Quoridor.App.Theme;
using Quoridor.Core;
using Quoridor.Engine;

namespace Quoridor.App.Views;

public partial class GameView : UserControl
{
    private readonly MainWindow _host;
    private GameSession _session;
    private readonly BoardView _board = new();
    private readonly PlayerCard[] _cards = new PlayerCard[2];

    private readonly DispatcherTimer? _clockTimer;
    private readonly Stopwatch _clockStopwatch = new();
    private readonly NetPeer? _peer;

    private CancellationTokenSource? _thinking;
    private bool _busy;

    /// <summary>Whether the engine loop is in the air. There is never more than one of it.</summary>
    private bool _engineRunning;

    /// <summary>
    /// Set when the view is on its way out. Cancelling the search only ends the move
    /// being thought about, so without this the loop would answer the cancellation by
    /// starting the next one and play a whole game nobody is watching.
    /// </summary>
    private bool _closed;

    /// <summary>Ply being looked at while stepping back through the game; null means live.</summary>
    private int? _reviewPly;

    public GameView(MainWindow host, GameOptions options, NetPeer? peer = null)
    {
        _host = host;
        _session = new GameSession(options);
        _session.PositionChanged += ResumeEngine;
        _peer = peer;

        InitializeComponent();

        if (_peer is not null)
        {
            _peer.Received += line => Dispatcher.Invoke(() => OnPeerMessage(line));
            _peer.Changed += () => Dispatcher.Invoke(UpdateUi);
        }

        ApplySeating();

        BoardHost.Children.Add(_board);
        _board.MoveChosen += OnMoveChosen;
        _board.WallPreviewChanged += (_, preview) => ShowWallPreview(preview);

        // Watching two engines is the one time the routes are the point.
        _board.Reading = Settings.Current.ShowRoutes || options.Mode == GameMode.Spectate;

        // The button was only ever coloured by being pressed, so a game that started with
        // reading already on showed it as off until it had been turned off and on again.
        PaintReadingButton();

        BuildThinkingDots();

        MenuButton.Click += (_, _) => LeaveToMenu();
        ResultMenuButton.Click += (_, _) => LeaveToMenu();
        FullscreenButton.Click += (_, _) => _host.ToggleFullscreen();
        UndoButton.Click += (_, _) => Undo();
        RestartButton.Click += (_, _) => Restart();
        RematchButton.Click += (_, _) => Restart();
        SwapSidesButton.Click += (_, _) => Restart(swap: true, broadcast: true);

        // Changing places only means anything when the two sides are different.
        if (options.Mode is GameMode.VersusBot or GameMode.Online)
            SwapSidesButton.Visibility = Visibility.Visible;
        RoutesButton.Click += (_, _) => ToggleRoutes();

        HelpButton.Click += (_, _) =>
        {
            _showHelp = !_showHelp;
            UpdateStatus();
        };

        SettingsButton.Click += (_, _) => ShowSettings(true);
        CloseSettingsButton.Click += (_, _) => ShowSettings(false);
        SettingsOverlay.MouseLeftButtonDown += (_, _) => ShowSettings(false);

        BuildDials();
        ReviewPrevButton.Click += (_, _) => StepReview(-1);
        ReviewNextButton.Click += (_, _) => StepReview(+1);
        ReviewLiveButton.Click += (_, _) => ReturnToLive();

        ThemeButton.Click += (_, _) =>
        {
            Palette.Toggle();
            OnThemeChanged();
        };

        Action<AppTheme> themeHandler = _ => OnThemeChanged();
        Palette.Changed += themeHandler;

        if (_session.HasClock)
        {
            _clockTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(100),
            };

            _clockTimer.Tick += OnClockTick;
        }

        Unloaded += (_, _) =>
        {
            Palette.Changed -= themeHandler;
            _clockTimer?.Stop();
            _closed = true;
            _thinking?.Cancel();
            _session.StopPondering();
            _peer?.Dispose();
        };

        Loaded += (_, _) =>
        {
            Focus();
            _board.Reset(_session.State, LastMove);
            UpdateUi();
            StartClock();
            StartEngineIfItsTurn();

            // The first move of the game is often the longest anyone thinks about it.
            StartPonderingIfItIsYourMove();
        };

        PreviewKeyDown += OnPreviewKeyDown;
        OnThemeChanged();
    }

    // ================================================================== flow ==

    /// <summary>
    /// A move made with the mouse. The caret goes with it: the pointer and the caret are
    /// the same player pointing at the same board, and one left standing on a square the
    /// player has just moved away from reads as a second thing also being chosen.
    /// </summary>
    private void OnMoveChosen(object? sender, Move move)
    {
        StopAiming();
        PlayHumanMove(move);
    }

    private async void PlayHumanMove(Move move)
    {
        try
        {
            if (_busy || !_session.IsHumanTurn) return;

            int ply = _session.Moves.Count;

            await PlayAsync(move);

            if (_peer is not null)
            {
                await _peer.SendAsync($"move|{ply}|{Notation.Format(move, _session.State)}");
                return;
            }

            StartEngineIfItsTurn();
        }
        catch (OperationCanceledException)
        {
            // Left the game or restarted mid-search; nothing to report.
        }
    }

    /// <summary>
    /// Sets the engine going again after the game has been moved somewhere nobody played
    /// to. This is what the session's <see cref="GameSession.PositionChanged"/> is wired
    /// to: whoever is on move after a rewind or a reset is not who was on move before, and
    /// leaving each of those paths to remember is how undoing a won game came to leave the
    /// engine on move with nothing to run it.
    ///
    /// Queued rather than answered on the spot, because the caller has not finished yet:
    /// it still has the board to rebuild, the log to redraw and the clock to restart, and
    /// its status line would otherwise land on top of the engine's "Thinking".
    /// </summary>
    private void ResumeEngine() => Dispatcher.InvokeAsync(StartEngineIfItsTurn);

    /// <summary>
    /// Takes over from the session this view was watching. Changing places builds a whole
    /// new one, and a new session nobody is listening to is exactly the silence this event
    /// exists to prevent.
    /// </summary>
    private void ReplaceSession(GameSession session)
    {
        _session.PositionChanged -= ResumeEngine;
        _session = session;
        _session.PositionChanged += ResumeEngine;
    }

    /// <summary>
    /// Kicks the engine off when it is on move — the far seat, or both in spectate.
    /// Does nothing while a loop is already in the air, even though the position it was
    /// started on has just changed: the loop re-reads the session between searches, so
    /// it finds the new position by itself, and a second loop would put two searches on
    /// one agent.
    /// </summary>
    private async void StartEngineIfItsTurn()
    {
        if (_engineRunning) return;

        _engineRunning = true;

        try
        {
            await RunEngineAsync();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _engineRunning = false;
        }
    }

    private async Task PlayAsync(Move move)
    {
        if (!_session.Apply(move)) return;

        // A pickup is the one thing on the board worth its own sound; without it the
        // most interesting move in the game sounds exactly like every other step.
        Sfx.Play(_session.LastMoveWentAgain ? Sound.Again
            : _session.LastMoveTookAWall ? Sound.Collect
            : _session.LastMoveCrossedPortal ? Sound.Portal
            : move.Kind == MoveKind.Pawn ? Sound.Move
            : Sound.Wall);

        // A move can still arrive from the other player while the game is being read back,
        // and the board on screen is a position from earlier in it. Animating onto that
        // board drew the reviewed walls plus the new one and snapped the pawns to where
        // they stand now, under a status line still saying play is paused. The move is
        // kept — both copies have to agree — and the panels and the move list follow it;
        // the board is left where the reader put it and catches up on Live. A game that
        // ends here is announced on the way back, not over a position from earlier in it.
        if (_reviewPly is not null)
        {
            UpdateUi();
            return;
        }

        _busy = true;
        UpdateUi();

        await _board.PlayAsync(move, _session.State);

        _busy = false;
        UpdateUi();

        if (_session.IsOver) Finish();

        StartPonderingIfItIsYourMove();
    }

    /// <summary>
    /// Hands the time the human is about to spend to the engine, when there is a human
    /// on move to spend it. This side only decides when a ponder is worth starting:
    /// everything that moves the game on ends it by itself, inside the session.
    /// </summary>
    private void StartPonderingIfItIsYourMove()
    {
        if (_closed || _busy || _reviewPly is not null) return;

        _session.StartPondering();
    }

    /// <summary>
    /// Plays the engine's moves for as long as it is on move. Cancelling a search does
    /// not end this loop and does not start a new one anywhere else: the loop comes back
    /// round, reads the session as it now stands, and either searches the new position
    /// or stops. That is what keeps a restart from having two searches on one agent.
    /// </summary>
    private async Task RunEngineAsync()
    {
        while (!_closed && _session.IsBotTurn && _reviewPly is null)
        {
            _thinking?.Dispose();
            _thinking = new CancellationTokenSource();
            CancellationToken token = _thinking.Token;

            // What the answer will have to match to be worth playing.
            GameSession asked = _session;
            int ply = asked.Moves.Count;

            SetThinking(true);

            Move move;
            try
            {
                Task<Move> search = _session.ThinkAsync(token);

                // A floor on thinking time: an instant reply reads as a glitch rather
                // than a decision, even when the search really was that quick.
                TimeSpan floor = _session.Options.Mode == GameMode.Spectate
                    ? Settings.Current.WatchPace
                    : TimeSpan.FromMilliseconds(280);

                // WhenAll waits for every task and not just the first to fail, so once
                // this returns — cancelled or not — the search is over and the agent is
                // free for the next turn round the loop.
                await Task.WhenAll(search, Task.Delay(floor, token));
                move = search.Result;
            }
            catch (OperationCanceledException)
            {
                SetThinking(false);
                continue;
            }

            SetThinking(false);

            // An abandoned search still answers: the engine returns the best move it had
            // rather than throwing, and the floor above may already have elapsed, so the
            // cancellation goes unnoticed. The answer belongs to the position it was
            // asked about, so it is only played if that is still the position in front of
            // us — a restart or an undo makes it a move for a game that no longer exists.
            if (token.IsCancellationRequested ||
                !ReferenceEquals(asked, _session) ||
                asked.Moves.Count != ply ||
                _reviewPly is not null)
            {
                continue;
            }

            await PlayAsync(move);
        }
    }

    /// <summary>
    /// A move from the other side. The ply guards against a duplicate or a message that
    /// overtook another; anything that does not line up is dropped rather than guessed
    /// at, because both sides hold the same position and can afford to wait.
    /// </summary>
    private async void OnPeerMessage(string line)
    {
        try
        {
            if (line is "restart" or "restart|swap")
            {
                Restart(swap: line.EndsWith("swap", StringComparison.Ordinal), broadcast: false);
                return;
            }

            string[] parts = line.Split('|');

            // The seat test is the one that matters: legality alone is checked against
            // whoever is on move, so without it a message arriving on our own turn is
            // applied as our move, and the other copy gets to play both colours.
            if (parts.Length != 3 ||
                parts[0] != "move" ||
                _session.State.SideToMove == _session.Options.HumanSeat ||
                !int.TryParse(parts[1], out int ply) ||
                ply != _session.Moves.Count ||
                !Notation.TryParse(parts[2], out Move move, _session.State.GoalRow(0)))
            {
                return;
            }

            await PlayAsync(move);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Undo()
    {
        if (_busy) return;

        _thinking?.Cancel();

        // Before the move list gets shorter, not after: the review ply is an index into it.
        LeaveReview();

        if (!_session.Undo()) return;

        HideResult();
        _board.Reset(_session.State, LastMove);
        UpdateUi();
        StartClock();
        StartPonderingIfItIsYourMove();
    }

    /// <summary>
    /// Puts the local player on the near edge, whichever seat that is, and orders the
    /// two cards to match. Called again when the players change places.
    /// </summary>
    private void ApplySeating()
    {
        GameOptions options = _session.Options;

        // Titled from the board actually in play rather than from the one the menu chose:
        // a rolled game rolls again on a rematch, and a header still describing the last
        // board is worse than no header at all.
        ModeLabel.Text = options.TitleFor(_session.Setup);

        // Whoever the local player is sits at the near edge, so the board turns around
        // when they take the second seat.
        _board.Flipped = options.HumanSeat == 1;
        _board.Holes = _session.Holes;

        int nearPlayer = options.HumanSeat;
        int farPlayer = nearPlayer ^ 1;

        // Pickups can hand out walls beyond the starting supply, so leave room for them.
        int pips = Math.Min(Board.MaxWalls, options.Setup.Walls + (options.Setup.Pickups > 0 ? 4 : 0));

        _cards[farPlayer] = new PlayerCard(farPlayer == 0 ? Palette.Accent0 : Palette.Accent1, pips);
        _cards[nearPlayer] = new PlayerCard(nearPlayer == 0 ? Palette.Accent0 : Palette.Accent1, pips)
        {
            Margin = new Thickness(0, 22, 0, 0),
        };

        PlayerColumn.Children.Clear();
        PlayerColumn.Children.Add(_cards[farPlayer]);
        PlayerColumn.Children.Add(new Rectangle
        {
            Style = (Style)FindResource("Rule"),
            Margin = new Thickness(0, 22, 0, 0),
        });
        PlayerColumn.Children.Add(_cards[nearPlayer]);
    }

    private void Restart() => Restart(swap: false, broadcast: true);

    private void Restart(bool swap, bool broadcast)
    {
        // Rebuilding the board mid-animation would let the finishing animation write
        // the old position back over the fresh one.
        if (_busy) return;

        // Nothing on the new board is where it was, so the caret goes home with it rather
        // than being left standing over a square that now means something else.
        StopAiming();

        // Over the network a restart is a joint decision, so tell the other side.
        if (broadcast && _peer is not null) _ = _peer.SendAsync(swap ? "restart|swap" : "restart");

        // Asking, not waiting: a search can take seconds to notice, and this runs on the
        // UI thread. The board below is rebuilt straight away and the engine loop finds
        // it when the abandoned search finally answers.
        _thinking?.Cancel();

        // Restarting in place stops the ponder by itself; swapping sides replaces the
        // whole session, and the one being left behind would otherwise go on thinking
        // about a game nobody is playing any more.
        _session.StopPondering();

        // Before the move list is emptied, not after: the review ply is an index into it.
        LeaveReview();

        if (swap)
        {
            // A different seat changes the board's orientation and who the engine plays,
            // which is the whole session — so it is built again rather than reset.
            ReplaceSession(new GameSession(_session.Options with
            {
                HumanMovesFirst = !_session.Options.HumanMovesFirst,
            }));

            ApplySeating();

            // A session that has only just been built has never been anywhere else, so it
            // has no change to announce and the first engine turn is started from here.
            // Restarting in place goes the other way, through PositionChanged.
            ResumeEngine();
        }
        else
        {
            _session.Restart();

            // A rolled game comes back on a different board, so the holes the board draws
            // and the header describing it both have to be asked for again. Standard and
            // Custom land on the same board and this costs them a redraw of two labels.
            ApplySeating();
        }

        HideResult();
        _board.Reset(_session.State, LastMove);
        UpdateUi();
        StartClock();
        StartPonderingIfItIsYourMove();
    }

    private void LeaveToMenu()
    {
        _closed = true;
        _thinking?.Cancel();
        _session.StopPondering();
        _clockTimer?.Stop();
        _host.ShowMenu();
    }

    private void Finish()
    {
        _clockTimer?.Stop();

        // In a hotseat or a watched game nobody at this keyboard lost, so it is a fanfare
        // either way; against an opponent it depends on which seat is ours.
        bool ours = _session.Options.Mode is GameMode.Hotseat or GameMode.Spectate ||
                    _session.Winner == _session.Options.HumanSeat;

        Sfx.Play(ours ? Sound.Win : Sound.Lose);
        ShowResult(_session.Winner);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Held with Control the arrows always step through the game, which is what frees
        // the bare ones for the board below without taking the review away from anybody
        // who had learned it.
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.Z: Undo(); break;
                case Key.Left: StepReview(-1); break;
                case Key.Right: StepReview(+1); break;
                default: return;
            }

            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.None) return;

        // Escape puts back whatever the last press put you into: the settings card first,
        // then the caret, and only then the game itself.
        if (e.Key == Key.Escape)
        {
            if (_settingsOpen) ShowSettings(false);
            else if (_aiming) StopAiming();
            else LeaveToMenu();

            e.Handled = true;
            return;
        }

        // With the settings card open the keys belong to the card. Space in particular:
        // it is how a button under the finger is pressed, and taking it for the board
        // behind meant the card's own controls could not be reached without the mouse.
        if (_settingsOpen) return;

        switch (e.Key)
        {
            case Key.F2:
                Restart();
                break;

            case Key.Space:
                ToggleRoutes();
                break;

            // Left and Right mean the board when there is a move to aim and the game's
            // past when there is not. It is the same key doing the obvious thing in both
            // places: while the engine is thinking, or the game is being read back, there
            // is nothing on the board to point at — and during your own turn the moves
            // already played are a click away in the list beside you.
            case Key.Left when !CanAim:
                StepReview(-1);
                break;

            case Key.Right when !CanAim:
                StepReview(+1);
                break;

            // Up and Down have nothing to fall back on, so out of turn they are left to
            // whatever the focused control wanted them for.
            case Key.Up when !CanAim:
            case Key.Down when !CanAim:
                return;

            // Screen directions and not board ones. The board is turned a half turn for
            // the second seat, and a caret that turned with it would go left when it was
            // asked to go right, which is worse than having no caret at all.
            case Key.Up: StepAim(-1, 0); break;
            case Key.Down: StepAim(+1, 0); break;
            case Key.Left: StepAim(0, -1); break;
            case Key.Right: StepAim(0, +1); break;

            // Into the grooves, and once there, round. Which slot the wall goes in and
            // which way it lies are the same question asked twice, so they are the same
            // key pressed twice.
            case Key.W when CanAim:
                TurnWall();
                break;

            // R has always turned the wall under the pointer, and goes on doing that when
            // there is no caret out to turn instead.
            case Key.R:
                if (_aiming) TurnWall();
                else _board.ToggleWallOrientation();
                break;

            // Back to your own piece, from wherever forty presses have left the caret.
            case Key.Home when CanAim:
                RestAim();
                break;

            // Only while the caret is out. Otherwise Enter belongs to whatever button the
            // player has tabbed to — Rematch, on the card where they are most likely to
            // have reached for it.
            case Key.Enter when _aiming:
                PlayAim();
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    // ================================================================= aiming ==

    private MoveKind _aimKind = MoveKind.Pawn;
    private int _aimRow;
    private int _aimCol;

    /// <summary>Whether the caret is out. It arrives on the first key that wants it and
    /// leaves on Escape or on a move made with the mouse, so a player who never touches
    /// the keyboard never sees it.</summary>
    private bool _aiming;

    /// <summary>
    /// The board, if it can draw a caret yet. The cast goes through <see cref="object"/>
    /// because <c>BoardView</c> is sealed: a sealed type that does not implement an
    /// interface cannot be tested against it directly, and the control that draws the
    /// board is maintained apart from this screen. Everything else about keyboard play
    /// works without it — this is only where the caret is put.
    /// </summary>
    private IBoardAim? Caret => (object)_board as IBoardAim;

    /// <summary>The move the caret is standing on, as the move playing it would be.</summary>
    private Move Aim => new(_aimKind, _aimRow, _aimCol);

    /// <summary>Whether there is anything on the board to point at right now.</summary>
    private bool CanAim =>
        !_busy &&
        !_settingsOpen &&
        _reviewPly is null &&
        _session.IsHumanTurn &&
        ResultOverlay.Visibility != Visibility.Visible;

    /// <summary>
    /// Brings the caret out, resting it on the piece whose turn it is — which is where the
    /// next move is being thought about before it has been aimed anywhere. False when
    /// there is no move to make, and the key then belongs to whatever else wanted it.
    /// </summary>
    private bool StartAiming()
    {
        if (!CanAim) return false;

        if (!_aiming)
        {
            _aiming = true;
            RestOnOwnPawn();
        }

        ClampAim();
        return true;
    }

    private void StopAiming()
    {
        if (!_aiming) return;

        _aiming = false;
        Caret?.Aim(null, false);
        UpdateStatus();
    }

    private void RestOnOwnPawn()
    {
        int cell = _session.State.PawnOf(_session.State.SideToMove);

        _aimKind = MoveKind.Pawn;
        _aimRow = Board.RowOf(cell);
        _aimCol = Board.ColOf(cell);
    }

    /// <summary>
    /// Keeps the caret inside the game actually being played. A smaller board makes the
    /// point twice over: a caret left where a nine put it can be off a five entirely. The
    /// grooves stop one short of the squares, because the last row and column have no
    /// corner of their own for a wall to sit in.
    /// </summary>
    private void ClampAim()
    {
        int first = _session.State.GoalRow(0);
        int last = _session.State.GoalRow(1) - (_aimKind == MoveKind.Pawn ? 0 : 1);

        _aimRow = Math.Clamp(_aimRow, first, last);
        _aimCol = Math.Clamp(_aimCol, first, last);
    }

    /// <summary>
    /// Moves the caret one square, or one slot while it is in the grooves, and stops at
    /// the edge of the game rather than wrapping round it: a caret that comes out at the
    /// far side is a caret you have to go looking for.
    /// </summary>
    private void StepAim(int rows, int cols)
    {
        if (!CanAim) return;

        // The first press only brings the caret out, and it comes out on your own piece.
        // One that appeared already a square away from there would be a caret you had to
        // work out the origin of before you could steer it.
        if (!_aiming)
        {
            StartAiming();
            ShowAim();
            return;
        }

        if (_board.Flipped)
        {
            rows = -rows;
            cols = -cols;
        }

        _aimRow += rows;
        _aimCol += cols;

        ClampAim();
        ShowAim();
    }

    /// <summary>
    /// Takes the caret into the grooves, and turns the wall once it is there. A square's
    /// own slot is the corner below and to the right of it, which is the wall a player
    /// looking at that square is thinking about.
    /// </summary>
    private void TurnWall()
    {
        if (!StartAiming()) return;

        _aimKind = _aimKind switch
        {
            MoveKind.HorizontalWall => MoveKind.VerticalWall,
            MoveKind.VerticalWall => MoveKind.HorizontalWall,
            _ => MoveKind.HorizontalWall,
        };

        ClampAim();
        ShowAim();
    }

    private void RestAim()
    {
        if (!StartAiming()) return;

        RestOnOwnPawn();
        ShowAim();
    }

    private void PlayAim()
    {
        if (!_aiming || !CanAim || !_session.State.IsLegal(Aim)) return;

        // The caret is left where it is. After a step it is standing on the square the
        // pawn has just arrived at, and after a wall it is on the groove beside the one
        // just filled — both of which are where the next move is thought about from.
        PlayHumanMove(Aim);
    }

    /// <summary>
    /// Hands the caret to the board and says out loud what it is standing on. Called from
    /// every key that moves it and again from <see cref="UpdateUi"/>, so that the caret
    /// goes away while the turn is somebody else's and comes back when it returns —
    /// without the player having to find it again each time.
    /// </summary>
    private void ShowAim()
    {
        if (!_aiming || !CanAim)
        {
            Caret?.Aim(null, false);
            return;
        }

        Move aim = Aim;
        Caret?.Aim(aim, _session.State.IsLegal(aim));
        UpdateStatus();
    }

    /// <summary>
    /// What the caret is standing on, in the notation printed round the board and used by
    /// the move list — so a player who cannot see the board is given the same names as one
    /// who can, and the two of them can talk about the same game. For a wall it is the
    /// sentence the pointer already gets, which is the only question worth asking about a
    /// wall before playing it.
    /// </summary>
    private string AimLine()
    {
        GameState state = _session.State;
        Move aim = Aim;
        string spot = Notation.Format(aim, state);

        if (aim.Kind != MoveKind.Pawn)
        {
            string wall = $"Wall {spot[..^1]} {(aim.IsHorizontal ? "across" : "down")}";

            return state.IsWallLegal(aim.Kind, aim.Row, aim.Col)
                ? $"{wall} — {WallCost(aim)} Enter places it."
                : $"{wall} — no room for a wall there.";
        }

        string what = Occupant(aim.Cell);

        return state.IsPawnMoveLegal(aim.Row, aim.Col)
            ? $"{spot}, {what}. Enter steps here."
            : $"{spot}, {what}. Not a step from where you stand.";
    }

    /// <summary>
    /// What this wall would actually do, in steps added to each side's route. The same
    /// measurement the board makes for the pointer, made again here because the caret is
    /// not the pointer and the rules it is measured against are public.
    /// </summary>
    private string WallCost(Move wall)
    {
        GameState state = _session.State;

        int mover = state.SideToMove;
        int opponent = mover ^ 1;

        GameState probe = state;
        probe.PlaceWallUnchecked(wall.Kind, wall.Row, wall.Col);

        int theirs = PathFinder.Distance(probe, opponent) - PathFinder.Distance(state, opponent);
        int yours = PathFinder.Distance(probe, mover) - PathFinder.Distance(state, mover);

        string them = theirs switch
        {
            0 => "costs them nothing",
            1 => "costs them 1 step",
            _ => $"costs them {theirs} steps",
        };

        return yours == 0 ? $"{them}, and nothing to you." : $"{them}, and {yours} to you.";
    }

    /// <summary>What is standing on a square, in the words the rules card uses for it.</summary>
    private string Occupant(int cell)
    {
        GameState state = _session.State;
        UInt128 bit = Board.Bit(cell);

        if ((_session.Holes & bit) != 0) return "a gap";
        if (state.PawnOf(0) == cell) return $"{_session.PlayerName(0)} is here";
        if (state.PawnOf(1) == cell) return $"{_session.PlayerName(1)} is here";
        if ((state.WallPickups & bit) != 0) return "two spare walls";
        if ((state.SkipPickups & bit) != 0) return "a free move";
        if (state.IsPortalMouth(cell)) return "a portal";

        return "empty";
    }

    // ================================================================ review ==

    /// <summary>
    /// Steps back and forth through the game already played. The engines stop while you
    /// are looking — a watched game that carried on behind your back would put you back
    /// where you started every time you glanced away.
    /// </summary>
    private void StepReview(int delta)
    {
        if (_busy) return;

        int played = _session.Moves.Count;
        if (played == 0) return;

        int target = Math.Clamp((_reviewPly ?? played) + delta, 0, played);

        if (target == played)
        {
            ReturnToLive();
            return;
        }

        if (_reviewPly is null)
        {
            _thinking?.Cancel();

            // Review leaves the position alone, so the session has no reason to stop the
            // ponder on its own — but a search nobody can act on is a core spent on
            // nothing while you read the game back.
            _session.StopPondering();
        }

        _reviewPly = target;
        _clockTimer?.Stop();

        _board.IsInteractive = false;
        _board.Reset(_session.StateAfter(target), target > 0 ? _session.Moves[target - 1] : null);

        UpdateReviewChrome();
    }

    /// <summary>
    /// Drops out of review without redrawing anything. Restarting and undoing both cut
    /// the move list the review ply points into, so the ply has to go before the session
    /// changes under it. Both callers redraw and re-enable the board themselves through
    /// <see cref="UpdateUi"/>, which is what turns interaction back on.
    /// </summary>
    private void LeaveReview() => _reviewPly = null;

    private void ReturnToLive()
    {
        if (_reviewPly is null) return;

        _reviewPly = null;
        _board.Reset(_session.State, LastMove);

        UpdateUi();
        StartClock();

        // The other player may have finished the game while it was being read back, in
        // which case the result was held over until there was a live position to show it
        // across. Guarded on the overlay because reading a finished game back is allowed,
        // and coming out of that must not sound the fanfare a second time.
        if (_session.IsOver && ResultOverlay.Visibility != Visibility.Visible) Finish();

        StartEngineIfItsTurn();
        StartPonderingIfItIsYourMove();
    }

    private void UpdateReviewChrome()
    {
        int played = _session.Moves.Count;

        // A review ply past the end of the list means something shortened it while the
        // review was open. Treat the review as finished rather than indexing off the end:
        // this runs inside click and key handlers, where an exception closes the app.
        if (_reviewPly > played) _reviewPly = null;

        ReviewPrevButton.IsEnabled = played > 0 && (_reviewPly ?? played) > 0;
        ReviewNextButton.IsEnabled = _reviewPly is not null;
        ReviewLiveButton.IsEnabled = _reviewPly is not null;

        // Why each of them is dead, said where the pointer already is.
        ReviewPrevButton.ToolTip = played == 0 ? "No moves to step back through yet."
            : ReviewPrevButton.IsEnabled ? null
            : "This is the position the game started from.";

        // The two forward controls only mean anything from inside the game's past.
        string? live = _reviewPly is null ? "You are at the live position." : null;
        ReviewNextButton.ToolTip = live;
        ReviewLiveButton.ToolTip = live;

        if (_reviewPly is not { } ply)
        {
            // Nothing is being read back, so nothing is marked. Left set, the mark stayed
            // on the row the reader stopped at and then sat there for the rest of the game.
            HighlightLogRow(-1);
            return;
        }

        Say(StatusText, ply == 0 ? "Starting position" : $"After {Notation.Format(_session.Moves[ply - 1], _session.State)}");
        Say(HintText, $"Move {ply} of {played}. Play is paused — press Live to catch up.");
        EngineLine.Visibility = Visibility.Collapsed;

        HighlightLogRow(ply == 0 ? -1 : RowOfPly(ply));
        FollowMoves();
    }

    /// <summary>
    /// Marks the round being read back. Ink alone was quiet enough to lose in a list of
    /// eighty rows, so the row is also lit and given a bar in the gutter the other rows
    /// hold open for it.
    /// </summary>
    private void HighlightLogRow(int row)
    {
        for (int i = 0; i < LogPanel.Children.Count; i++)
        {
            bool here = i == row;
            var line = (Border)LogPanel.Children[i];

            RowText(i).Foreground = Palette.BrushOf(here ? Palette.Text : Palette.Muted);
            line.BorderBrush = here ? Palette.BrushOf(Palette.Accent0) : Brushes.Transparent;

            // Cleared rather than set to transparent, so that a row nobody is reading goes
            // back to answering the hover trigger in the style.
            if (here) line.Background = Palette.BrushOf(Palette.Cell);
            else line.ClearValue(Border.BackgroundProperty);
        }
    }

    private void ToggleRoutes()
    {
        _board.Reading = !_board.Reading;

        // Kept, not just applied. The menu's own setting has always been written down, so
        // turning reading on from inside a game and finding it off in the next one was the
        // two controls disagreeing about the same preference.
        Settings.Current.ShowRoutes = _board.Reading;

        PaintReadingButton();
    }

    private void PaintReadingButton() =>
        RoutesButton.Foreground = Palette.BrushOf(_board.Reading ? Palette.Accent0 : Palette.Text);

    // ============================================================== settings ==

    private bool _settingsOpen;

    /// <summary>
    /// Wires the two volume dials. Dragging one applies immediately — a volume you have
    /// to confirm before you can hear it is a volume you cannot set — and the effects
    /// dial plays a sound as it moves, so what you are setting is audible while you set it.
    /// </summary>
    private void BuildDials()
    {
        Settings settings = Settings.Current;

        SoundDial.Value = settings.SoundVolume;
        MusicDial.Value = settings.MusicVolume;

        SoundReading.Text = $"{settings.SoundVolume}%";
        MusicReading.Text = $"{settings.MusicVolume}%";

        SoundDial.ValueChanged += (_, e) =>
        {
            int level = (int)Math.Round(e.NewValue);

            settings.SoundVolume = level;
            settings.Sound = level > 0;
            SoundReading.Text = $"{level}%";

            Sfx.RefreshVolumes();
            Sfx.Play(Sound.Move);
        };

        TrackPick.SelectedIndex = Math.Clamp(settings.MusicTrack, 0, 2);
        TrackPick.SelectionChanged += (_, _) =>
        {
            settings.MusicTrack = TrackPick.SelectedIndex;

            // Changing the piece is only audible if it is playing.
            if (!settings.Music && settings.MusicVolume <= 0) return;

            settings.Music = true;
            Sfx.Music(true);
        };

        MusicDial.ValueChanged += (_, e) =>
        {
            int level = (int)Math.Round(e.NewValue);

            settings.MusicVolume = level;
            MusicReading.Text = $"{level}%";

            // Above zero the music should be playing; at zero there is nothing to hear,
            // so it is stopped rather than left running silently.
            bool wanted = level > 0;
            if (wanted != settings.Music)
            {
                settings.Music = wanted;
                Sfx.Music(wanted);
            }

            Sfx.RefreshVolumes();
        };
    }

    private void ShowSettings(bool visible)
    {
        if (_settingsOpen == visible) return;

        _settingsOpen = visible;

        if (!visible)
        {
            Settings.Current.Save();
        }
        else
        {
            SettingsNote.Text = _session.Options.Setup.Pickups > 0
                ? $"This game: {_session.Options.Setup.Describe()}. A bar on a square is a spare " +
                  "wall; a ring is a free move, which does not pass the turn. The rules screen " +
                  "in the menu has both in full."
                : $"This game: {_session.Options.Setup.Describe()}.";
            SettingsOverlay.Visibility = Visibility.Visible;
        }

        var fade = new DoubleAnimation(visible ? 1 : 0, TimeSpan.FromMilliseconds(visible ? 220 : 160));
        if (!visible) fade.Completed += (_, _) => SettingsOverlay.Visibility = Visibility.Collapsed;

        SettingsOverlay.BeginAnimation(OpacityProperty, fade);

        var grow = new DoubleAnimation(visible ? 1 : 0.97, TimeSpan.FromMilliseconds(visible ? 320 : 160))
        {
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut },
        };

        SettingsScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        SettingsScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    // ================================================================= clock ==

    private void StartClock()
    {
        if (_clockTimer is null) return;

        _clockStopwatch.Restart();

        if (_session.IsOver) _clockTimer.Stop();
        else _clockTimer.Start();
    }

    private void OnClockTick(object? sender, EventArgs e)
    {
        TimeSpan elapsed = _clockStopwatch.Elapsed;
        _clockStopwatch.Restart();

        bool flagged = _session.ChargeClock(elapsed);

        UpdateCards();

        if (flagged) Finish();
    }

    // ================================================================== view ==

    private void UpdateUi()
    {
        UpdateCards();

        UndoButton.IsEnabled = _session.CanUndo && !_busy;
        UndoButton.ToolTip = UndoNote;
        RestartButton.IsEnabled = !_busy;

        _board.IsInteractive = !_busy && _session.IsHumanTurn && _reviewPly is null;

        RebuildLog();
        UpdateStatus();
        UpdateEngineLine();
        UpdateReviewChrome();

        // After the status, because the caret has the last word on the hint line; and on
        // every pass, so that it goes away while the turn is somebody else's and comes
        // back when it returns rather than having to be found again.
        ShowAim();
    }

    /// <summary>
    /// Why Undo is not available, when it is not. Over the network it never is, and that
    /// is a rule of the mode rather than of the position, so it is worth saying even though
    /// the button will not come back to life in that game.
    /// </summary>
    private string? UndoNote
    {
        get
        {
            if (_busy) return "The board is in the middle of a move.";
            if (_session.CanUndo) return null;

            if (_session.Options.Mode == GameMode.Online)
                return "Taking a move back has to be agreed, and there is no way to ask.";

            return _session.Moves.Count == 0
                ? "Nothing has been played yet."
                : "Not until you have had a move of your own to take back.";
        }
    }

    private void UpdateCards()
    {
        GameState state = _session.State;
        (int first, int second) = _session.Distances();

        for (int player = 0; player < 2; player++)
        {
            string? clock = _session.HasClock ? TimeControl.Format(_session.RemainingOf(player)) : null;

            _cards[player].Update(
                _session.PlayerName(player),
                state.WallsOf(player),
                player == 0 ? first : second,
                !_session.IsOver && state.SideToMove == player,
                clock);
        }

        UpdateTape(first, second);
    }

    // =================================================================== tape ==

    /// <summary>
    /// How far ahead a player has to be before the bar stops leaning further. Past six
    /// steps the answer is simply "them", and a longer bar would be saying the same thing
    /// louder rather than saying more.
    /// </summary>
    private const int DecisiveLead = 6;

    /// <summary>
    /// The race, drawn. Each side's remaining steps sit at its own end, and the bar leans
    /// toward whoever gets home first by as much as the lead is worth.
    ///
    /// The half-step of being on move is counted, because the player about to walk one of
    /// those steps is that much nearer than the count says. It is also why the bar is never
    /// exactly level, and why the turn passing is visible here as well as on the board.
    /// </summary>
    private void UpdateTape(int first, int second)
    {
        // Set outright rather than through Say: these are not a live region. The whole
        // figure is named for a reader, and two numbers read out on every clock tick would
        // bury the one line that is worth hearing.
        TapeFirst.Text = Remaining(first);
        TapeSecond.Text = Remaining(second);

        double lead;

        // Having no route at all is not being a long way behind; it is being out of the
        // race, and the figure should say so rather than pick a number.
        if (first < 0 && second < 0) lead = 0;
        else if (first < 0) lead = -DecisiveLead;
        else if (second < 0) lead = DecisiveLead;
        else lead = second - first + (_session.IsOver ? 0 : _session.State.SideToMove == 0 ? 0.5 : -0.5);

        int ahead = lead >= 0 ? 0 : 1;
        double edge = Math.Min(Math.Abs(lead) / DecisiveLead, 1);

        // The whole sentence goes on the figure, which is why nothing is written beside it.
        string race = Race(first, second);
        Tape.ToolTip = race;
        AutomationProperties.SetName(Tape, race);

        // The cards are redrawn ten times a second while a clock is running, and an
        // animation relaunched on every one of those never finishes: the bar would creep
        // toward its target and stop short of it for as long as the game lasted.
        if ((ahead, edge) == _tapeAt) return;

        _tapeAt = (ahead, edge);

        // One bar and not two, so the turn passing slides the same shape across the middle.
        // Which half of the rail it lives in is also which edge it grows from.
        Grid.SetColumn(TapeWedge, ahead);
        TapeWedge.RenderTransformOrigin = new Point(ahead == 0 ? 1 : 0, 0.5);
        TapeWedge.Fill = Palette.BrushOf(ahead == 0 ? Palette.Accent0 : Palette.Accent1);

        TapeWedgeScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(edge, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    /// <summary>Where the bar was last sent, so it is only sent somewhere new.</summary>
    private (int Ahead, double Edge) _tapeAt = (-1, -1);

    /// <summary>What a player has left to walk, short enough to sit at the end of the bar.</summary>
    private static string Remaining(int distance) =>
        distance < 0 ? "—" : distance == 0 ? "home" : distance.ToString();

    /// <summary>
    /// The figure said out loud, for the tooltip and for anyone reading the screen rather
    /// than looking at it. A shape with a sentence attached needs no caption printed beside it.
    /// </summary>
    private string Race(int first, int second)
    {
        string both = $"{_session.PlayerName(0)} {Walk(first)}, {_session.PlayerName(1)} {Walk(second)}";

        if (first < 0 || second < 0) return $"{both}.";

        int lead = Math.Abs(second - first);

        if (lead == 0)
        {
            return _session.IsOver
                ? $"{both} — level."
                : $"{both} — level, and {_session.PlayerName(_session.State.SideToMove)} to move.";
        }

        return $"{both} — {_session.PlayerName(second > first ? 0 : 1)} ahead by {lead} {(lead == 1 ? "step" : "steps")}.";
    }

    private static string Walk(int distance) => distance switch
    {
        < 0 => "has no route",
        0 => "is home",
        1 => "is one step from home",
        _ => $"is {distance} steps from home",
    };

    /// <summary>
    /// Whether the how-to-move line has been asked for. It retires on its own after a
    /// couple of moves — by then it has been learned by doing, and a line that never
    /// changes is a line you stop reading — and the mark by the heading brings it back.
    /// </summary>
    private bool _showHelp;

    /// <summary>
    /// Writes a line and, if anything is listening, says that it changed. WPF raises
    /// nothing by itself when a live region's text is set from code, and the guard is not
    /// only for the cost: the status is rewritten several times a second while a wall is
    /// being aimed, and a reader that heard every one of those would be unusable.
    /// </summary>
    private static void Say(TextBlock block, string text)
    {
        if (block.Text == text) return;

        block.Text = text;
        UIElementAutomationPeer.FromElement(block)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void UpdateStatus()
    {
        WriteStatus();

        // The caret has the last word on the hint line, because while it is out that line
        // is what the caret is: where it stands, what is on that square, and what playing
        // there would cost.
        if (_aiming && CanAim) Say(HintText, AimLine());

        // A shortcut nobody is told about is a shortcut nobody uses, and a list of them
        // standing over the board all game is exactly the furniture the help line retired
        // for being. So they are shown with the help and not otherwise.
        KeysText.Visibility = _showHelp && !_session.IsOver ? Visibility.Visible : Visibility.Collapsed;
        KeysText.Text = "Arrows aim  ·  Enter plays  ·  W walls, R turns  ·  Home your pawn\n" +
                        "Space reads  ·  Ctrl+← → step the game  ·  Ctrl+Z undo  ·  Esc back";
    }

    private void WriteStatus()
    {
        GameState state = _session.State;

        if (_session.IsOver)
        {
            Say(StatusText, WinnerLabel(_session.Winner));
            Say(HintText, "Rematch to play again, or head back to the menu.");
            return;
        }

        int side = state.SideToMove;

        // Without saying so, a turn that does not pass just looks like the game ignored
        // the other player.
        string advice = _session.LastMoveWentAgain
            ? "Free move — the turn does not pass. Go again."
            : _session.LastTurnForfeited
            ? "Your opponent has no legal move. The turn is forfeited — play again."
            : _session.LastMoveTookAWall
                ? "Two spare walls picked up, on top of what you started with."
                : _session.LastMoveCrossedPortal
                ? "Through the portal — one step to the square it is linked to."
                : state.WallsOf(side) == 0
                    ? "No walls left — it is a straight race now."
                    : Instructions;

        if (_peer is not null)
        {
            if (!_peer.IsConnected)
            {
                Say(StatusText, "Disconnected");
                Say(HintText, _peer.Trouble.Length > 0 ? _peer.Trouble : "The link to the other player is gone.");
                return;
            }

            Say(StatusText, _session.IsHumanTurn ? "Your move" : "Waiting for your opponent");
            Say(HintText, advice);
            return;
        }

        if (_session.Options.Mode == GameMode.Spectate)
        {
            Say(StatusText, $"{_session.PlayerName(side)} to move");
            Say(HintText, "Both sides are played by the engine. Routes are drawn for each.");
            return;
        }

        Say(StatusText, _session.IsHumanTurn && _session.Options.Mode == GameMode.VersusBot
            ? "Your move"
            : $"{_session.PlayerName(side)} to move");

        Say(HintText, advice);
    }

    /// <summary>
    /// How to move, while it is still worth saying. Everything else on this line is about
    /// the position and changes with it; this one sentence never changes, which is why it
    /// is the one that goes.
    /// </summary>
    private string Instructions => _showHelp || _session.Moves.Count <= 2
        ? "Click a square to step, or hover a groove between squares to place a wall. Arrows and Enter play it from the keyboard."
        : string.Empty;

    /// <summary>
    /// Answers the only question that matters about a wall before you commit to it:
    /// how many steps does it actually cost, and does it cost you as well.
    /// </summary>
    private void ShowWallPreview(BoardView.WallPreview? preview)
    {
        if (preview is not { } wall)
        {
            UpdateStatus();
            return;
        }

        if (!wall.Legal)
        {
            Say(HintText, wall.WouldSeal
                ? "That would leave a player with no route at all — not allowed."
                : "No room for a wall there.");
            return;
        }

        string opponent = wall.CostToOpponent switch
        {
            0 => "Costs them nothing",
            1 => "Costs them 1 step",
            _ => $"Costs them {wall.CostToOpponent} steps",
        };

        Say(HintText, wall.CostToMover == 0
            ? $"{opponent}, and nothing to you."
            : $"{opponent}, and {wall.CostToMover} to you.");
    }

    private void RebuildLog()
    {
        IReadOnlyList<Move> moves = _session.Moves;
        int rows = (moves.Count + 1) / 2;

        LogEmpty.Visibility = moves.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        while (LogPanel.Children.Count > rows)
            LogPanel.Children.RemoveAt(LogPanel.Children.Count - 1);

        while (LogPanel.Children.Count < rows)
        {
            int index = LogPanel.Children.Count;

            var line = new Border
            {
                Style = (Style)FindResource("LogRow"),
                Opacity = 0,
                Child = new TextBlock { Style = (Style)FindResource("Text.Mono") },
            };

            // Clicking a row shows the position as it stood after that round.
            line.MouseLeftButtonDown += (_, _) =>
                StepReview(Math.Min((index + 1) * 2, _session.Moves.Count) - (_reviewPly ?? _session.Moves.Count));

            LogPanel.Children.Add(line);
            line.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
        }

        for (int row = 0; row < rows; row++)
        {
            string first = Notation.Format(moves[row * 2], _session.State);
            string second = row * 2 + 1 < moves.Count ? Notation.Format(moves[row * 2 + 1], _session.State) : string.Empty;
            RowText(row).Text = $"{row + 1,2}.  {first,-6}{second}";
        }

        FollowMoves();
    }

    private TextBlock RowText(int row) => (TextBlock)((Border)LogPanel.Children[row]).Child;

    /// <summary>
    /// The row of the list this pair of plies is written on. Half a move down, since the
    /// list is a round to a line.
    /// </summary>
    private static int RowOfPly(int ply) => ply <= 0 ? 0 : (ply - 1) / 2;

    /// <summary>Where the list was last left, so it is only moved when it has to be.</summary>
    private (int Played, int? Reviewing) _listed = (-1, null);

    /// <summary>
    /// Keeps the row that matters in view. The list is a fixed height and a game outgrows
    /// it about ten moves in, after which the move just played is below the fold —
    /// including the one the review has just stepped to, which is the whole point of
    /// having stepped. It used to be scrolled to the end unconditionally, so reading the
    /// game back scrolled the row you were reading away from you.
    ///
    /// Deferred to the end of the layout pass, because a row added this moment has no
    /// height yet and so no position to scroll to.
    /// </summary>
    private void FollowMoves()
    {
        (int, int?) now = (_session.Moves.Count, _reviewPly);
        if (now == _listed) return;

        _listed = now;

        Dispatcher.InvokeAsync(() =>
        {
            int rows = LogPanel.Children.Count;
            if (rows == 0) return;

            int wanted = _reviewPly is { } ply ? RowOfPly(ply) : rows - 1;
            if (wanted >= rows) return;

            var row = (Border)LogPanel.Children[wanted];
            double top = row.TranslatePoint(new Point(0, 0), LogPanel).Y;
            double bottom = top + row.ActualHeight;

            // Only as far as it has to go. A row already on screen is left where it is,
            // so following the game does not shuffle the list under a reader's eye.
            double offset = LogScroller.VerticalOffset;

            if (top < offset) offset = top;
            else if (bottom > offset + LogScroller.ViewportHeight) offset = bottom - LogScroller.ViewportHeight;
            else return;

            SmoothScroll.To(LogScroller, offset, TimeSpan.FromMilliseconds(260));
        }, DispatcherPriority.Loaded);
    }

    private void OnThemeChanged()
    {
        ThemeButton.Content = Palette.Current == AppTheme.Dark ? "Light" : "Dark";
        foreach (PlayerCard card in _cards) card?.RefreshAccent();
    }

    private Move? LastMove => _session.Moves.Count > 0 ? _session.Moves[^1] : null;

    /// <summary>
    /// What the engine actually did on its last turn. It costs one line and it is the
    /// difference between "the computer moved" and being able to see it thinking.
    /// </summary>
    private void UpdateEngineLine()
    {
        int lastMover = _session.State.SideToMove ^ 1;

        if (_session.AgentOf(lastMover) is not SearchAgent engine || engine.LastResult.Depth == 0)
        {
            EngineLine.Visibility = Visibility.Collapsed;
            return;
        }

        SearchResult result = engine.LastResult;
        string mover = _session.PlayerName(lastMover);

        string verdict = result.IsForced
            ? result.Score > 0 ? $"{mover} has a forced win" : $"{mover} is lost"
            : result.Score > 250 ? $"{mover} ahead"
            : result.Score < -250 ? $"{mover} behind"
            : "level";

        EngineLine.Visibility = Visibility.Visible;
        EngineLine.Text = $"depth {result.Depth} · {FormatNodes(result.Nodes)} nodes · {verdict}";
    }

    private static string FormatNodes(long nodes) => nodes switch
    {
        >= 1_000_000 => $"{nodes / 1_000_000.0:F1}M",
        >= 1_000 => $"{nodes / 1000.0:F0}k",
        _ => nodes.ToString(),
    };

    // ============================================================== thinking ==

    private void BuildThinkingDots()
    {
        for (int i = 0; i < 3; i++)
        {
            var dot = new Ellipse
            {
                Width = 4,
                Height = 4,
                Margin = new Thickness(0, 0, 4, 0),
                Fill = Palette.BrushOf(Palette.Muted),
                Opacity = 0.25,
            };

            dot.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(620))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(i * 170),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });

            ThinkingDots.Children.Add(dot);
        }
    }

    private void SetThinking(bool thinking)
    {
        ThinkingDots.Visibility = thinking ? Visibility.Visible : Visibility.Collapsed;

        if (!thinking) return;

        Say(StatusText, "Thinking");

        Say(HintText, _session.Options.Mode == GameMode.Spectate
            ? "Both sides are played by the engine. Routes are drawn for each."
            : "The engine is looking for the move that stretches your route the most.");
    }

    // ================================================================ result ==

    private string WinnerLabel(int winner)
    {
        bool onTime = _session.FlaggedPlayer >= 0;
        string suffix = onTime ? " on time" : string.Empty;

        if (_session.Options.Mode == GameMode.VersusBot)
            return (winner == _session.Options.HumanSeat ? "You win" : "The bot wins") + suffix;

        return $"{_session.PlayerName(winner)} wins{suffix}";
    }

    private void ShowResult(int winner)
    {
        ResultTitle.Text = WinnerLabel(winner);
        ResultDetail.Text = $"{_session.Moves.Count} moves  ·  {_session.State.WallsOf(winner)} walls unspent";

        ResultOverlay.Visibility = Visibility.Visible;

        // The pawn has just landed on the far row and that is the move the whole game was
        // about, so the card waits long enough for it to be seen. A quarter of a second was
        // the pawn arriving and the board being taken away in the same glance.
        var delay = TimeSpan.FromMilliseconds(750);

        ResultOverlay.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(340)) { BeginTime = delay });

        ResultShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(560))
            {
                BeginTime = delay,
                EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut },
            });

        // The board used to be blurred behind the card. A blur over the whole board is a
        // full-screen filter re-rasterised every frame, and — worse — it takes away the
        // position at the one moment a player wants to look at it. The wash the overlay
        // already carries is enough to say what is in front.
    }

    private void HideResult()
    {
        if (ResultOverlay.Visibility != Visibility.Visible) return;

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => ResultOverlay.Visibility = Visibility.Collapsed;
        ResultOverlay.BeginAnimation(OpacityProperty, fade);
    }
}
