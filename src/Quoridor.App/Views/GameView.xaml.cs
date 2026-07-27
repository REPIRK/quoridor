using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
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

    /// <summary>Ply being looked at while stepping back through the game; null means live.</summary>
    private int? _reviewPly;

    public GameView(MainWindow host, GameOptions options, NetPeer? peer = null)
    {
        _host = host;
        _session = new GameSession(options);
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
        _board.ShowRoutes = Settings.Current.ShowRoutes || options.Mode == GameMode.Spectate;

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
            _thinking?.Cancel();
            _peer?.Dispose();
        };

        Loaded += (_, _) =>
        {
            Focus();
            _board.Reset(_session.State, LastMove);
            UpdateUi();
            StartClock();
            StartEngineIfItsTurn();
        };

        PreviewKeyDown += OnPreviewKeyDown;
        OnThemeChanged();
    }

    // ================================================================== flow ==

    private async void OnMoveChosen(object? sender, Move move)
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

            await RunEngineAsync();
        }
        catch (OperationCanceledException)
        {
            // Left the game or restarted mid-search; nothing to report.
        }
    }

    /// <summary>Kicks the engine off when it is on move — the far seat, or both in spectate.</summary>
    private async void StartEngineIfItsTurn()
    {
        try
        {
            await RunEngineAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PlayAsync(Move move)
    {
        if (!_session.Apply(move)) return;

        Sfx.Play(move.Kind == MoveKind.Pawn ? Sound.Move : Sound.Wall);

        _busy = true;
        UpdateUi();

        await _board.PlayAsync(move, _session.State);

        _busy = false;
        UpdateUi();

        if (_session.IsOver) Finish();
    }

    private async Task RunEngineAsync()
    {
        while (_session.IsBotTurn && _reviewPly is null)
        {
            _thinking?.Dispose();
            _thinking = new CancellationTokenSource();
            CancellationToken token = _thinking.Token;

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

                await Task.WhenAll(search, Task.Delay(floor, token));
                move = search.Result;
            }
            catch (OperationCanceledException)
            {
                SetThinking(false);
                return;
            }

            SetThinking(false);
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

            if (parts.Length != 3 ||
                parts[0] != "move" ||
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
        if (!_session.Undo()) return;

        HideResult();
        _board.Reset(_session.State, LastMove);
        UpdateUi();
        StartClock();
    }

    /// <summary>
    /// Puts the local player on the near edge, whichever seat that is, and orders the
    /// two cards to match. Called again when the players change places.
    /// </summary>
    private void ApplySeating()
    {
        GameOptions options = _session.Options;

        ModeLabel.Text = options.Title;

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

        // Over the network a restart is a joint decision, so tell the other side.
        if (broadcast && _peer is not null) _ = _peer.SendAsync(swap ? "restart|swap" : "restart");

        _thinking?.Cancel();

        if (swap)
        {
            // A different seat changes the board's orientation and who the engine plays,
            // which is the whole session — so it is built again rather than reset.
            _session = new GameSession(_session.Options with
            {
                HumanMovesFirst = !_session.Options.HumanMovesFirst,
            });

            ApplySeating();
        }
        else
        {
            _session.Restart();
        }

        HideResult();
        _board.Reset(_session.State, LastMove);
        UpdateUi();
        StartClock();
        StartEngineIfItsTurn();
    }

    private void LeaveToMenu()
    {
        _thinking?.Cancel();
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
        switch (e.Key)
        {
            case Key.Escape:
                if (_settingsOpen) ShowSettings(false);
                else LeaveToMenu();
                e.Handled = true;
                break;

            case Key.Z when Keyboard.Modifiers == ModifierKeys.Control:
                Undo();
                e.Handled = true;
                break;

            case Key.F2:
                Restart();
                e.Handled = true;
                break;

            case Key.R:
                _board.ToggleWallOrientation();
                e.Handled = true;
                break;

            case Key.Space:
                ToggleRoutes();
                e.Handled = true;
                break;

            case Key.Left:
                StepReview(-1);
                e.Handled = true;
                break;

            case Key.Right:
                StepReview(+1);
                e.Handled = true;
                break;
        }
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

        if (_reviewPly is null) _thinking?.Cancel();

        _reviewPly = target;
        _clockTimer?.Stop();

        _board.IsInteractive = false;
        _board.Reset(_session.StateAfter(target), target > 0 ? _session.Moves[target - 1] : null);

        UpdateReviewChrome();
    }

    private void ReturnToLive()
    {
        if (_reviewPly is null) return;

        _reviewPly = null;
        _board.Reset(_session.State, LastMove);

        UpdateUi();
        StartClock();
        StartEngineIfItsTurn();
    }

    private void UpdateReviewChrome()
    {
        int played = _session.Moves.Count;

        ReviewPrevButton.IsEnabled = played > 0 && (_reviewPly ?? played) > 0;
        ReviewNextButton.IsEnabled = _reviewPly is not null;
        ReviewLiveButton.IsEnabled = _reviewPly is not null;

        if (_reviewPly is not { } ply) return;

        StatusText.Text = ply == 0 ? "Starting position" : $"After {Notation.Format(_session.Moves[ply - 1], _session.State)}";
        HintText.Text = $"Move {ply} of {played}. Play is paused — press Live to catch up.";
        EngineLine.Visibility = Visibility.Collapsed;

        HighlightLogRow(ply == 0 ? -1 : (ply - 1) / 2);
    }

    private void HighlightLogRow(int row)
    {
        for (int i = 0; i < LogPanel.Children.Count; i++)
        {
            ((TextBlock)LogPanel.Children[i]).Foreground = i == row
                ? Palette.BrushOf(Palette.Text)
                : Palette.BrushOf(Palette.Muted);
        }
    }

    private void ToggleRoutes()
    {
        _board.ShowRoutes = !_board.ShowRoutes;

        RoutesButton.Foreground = _board.ShowRoutes
            ? Palette.BrushOf(Palette.Accent0)
            : Palette.BrushOf(Palette.Text);
    }

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
        RestartButton.IsEnabled = !_busy;

        _board.IsInteractive = !_busy && _session.IsHumanTurn && _reviewPly is null;

        RebuildLog();
        UpdateStatus();
        UpdateEngineLine();
        UpdateReviewChrome();
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
    }

    private void UpdateStatus()
    {
        GameState state = _session.State;

        if (_session.IsOver)
        {
            StatusText.Text = WinnerLabel(_session.Winner);
            HintText.Text = "Rematch to play again, or head back to the menu.";
            return;
        }

        int side = state.SideToMove;

        if (_peer is not null)
        {
            if (!_peer.IsConnected)
            {
                StatusText.Text = "Disconnected";
                HintText.Text = _peer.Trouble.Length > 0 ? _peer.Trouble : "The link to the other player is gone.";
                return;
            }

            StatusText.Text = _session.IsHumanTurn ? "Your move" : "Waiting for your opponent";
            HintText.Text = state.WallsOf(side) == 0
                ? "No walls left — it is a straight race now."
                : "Click a square to step, or hover a groove between squares to place a wall.";
            return;
        }

        if (_session.Options.Mode == GameMode.Spectate)
        {
            StatusText.Text = $"{_session.PlayerName(side)} to move";
            HintText.Text = "Both sides are played by the engine. Routes are drawn for each.";
            return;
        }

        StatusText.Text = _session.IsHumanTurn && _session.Options.Mode == GameMode.VersusBot
            ? "Your move"
            : $"{_session.PlayerName(side)} to move";

        HintText.Text = state.WallsOf(side) == 0
            ? "No walls left — it is a straight race now."
            : "Click a square to step, or hover a groove between squares to place a wall.";
    }

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
            HintText.Text = wall.WouldSeal
                ? "That would leave a player with no route at all — not allowed."
                : "No room for a wall there.";
            return;
        }

        string opponent = wall.CostToOpponent switch
        {
            0 => "Costs them nothing",
            1 => "Costs them 1 step",
            _ => $"Costs them {wall.CostToOpponent} steps",
        };

        HintText.Text = wall.CostToMover == 0
            ? $"{opponent}, and nothing to you."
            : $"{opponent}, and {wall.CostToMover} to you.";
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

            var line = new TextBlock
            {
                Style = (Style)FindResource("Text.Mono"),
                Margin = new Thickness(0, 0, 0, 5),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Opacity = 0,
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
            ((TextBlock)LogPanel.Children[row]).Text = $"{row + 1,2}.  {first,-6}{second}";
        }

        LogScroller.ScrollToEnd();
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

        StatusText.Text = "Thinking";

        HintText.Text = _session.Options.Mode == GameMode.Spectate
            ? "Both sides are played by the engine. Routes are drawn for each."
            : "The engine is looking for the move that stretches your route the most.";
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

        var delay = TimeSpan.FromMilliseconds(240);

        ResultOverlay.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(340)) { BeginTime = delay });

        ResultShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(560))
            {
                BeginTime = delay,
                EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut },
            });

        // Pushing the board out of focus puts the result where the eye already is.
        var blur = new BlurEffect { Radius = 0 };
        BoardHost.Effect = blur;
        blur.BeginAnimation(BlurEffect.RadiusProperty,
            new DoubleAnimation(0, 13, TimeSpan.FromMilliseconds(460)) { BeginTime = TimeSpan.FromMilliseconds(180) });
    }

    private void HideResult()
    {
        if (ResultOverlay.Visibility != Visibility.Visible) return;

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => ResultOverlay.Visibility = Visibility.Collapsed;
        ResultOverlay.BeginAnimation(OpacityProperty, fade);

        if (BoardHost.Effect is BlurEffect blur)
        {
            var clear = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
            clear.Completed += (_, _) => BoardHost.Effect = null;
            blur.BeginAnimation(BlurEffect.RadiusProperty, clear);
        }
    }
}
