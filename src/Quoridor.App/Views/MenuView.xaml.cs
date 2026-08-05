using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Quoridor.App.Controls;
using Quoridor.App.Game;
using Quoridor.App.Theme;
using Quoridor.Core;
using Quoridor.Engine;

namespace Quoridor.App.Views;

public partial class MenuView : UserControl
{
    private readonly MainWindow _host;
    private readonly BoardView _demo = new();

    /// <summary>
    /// The real engine on both sides, on a short clock. A one-ply agent almost never
    /// spends a wall, so the demo it produces is two dots walking past each other —
    /// which is not what the game looks like.
    /// </summary>
    private readonly IQuoridorAgent[] _demoAgents =
    {
        new SearchAgent(maxDepth: 10, moveTime: TimeSpan.FromMilliseconds(90), threads: 1, tableMegabytes: 4),
        new SearchAgent(maxDepth: 10, moveTime: TimeSpan.FromMilliseconds(90), threads: 1, tableMegabytes: 4),
    };

    private CancellationTokenSource? _demoCancellation;
    private NetPeer? _peer;
    private Action? _peerChanged;
    private bool _handedOver;
    private bool _rulesOpen;
    private bool _settingsOpen;
    private bool _networkOpen;

    public MenuView(MainWindow host)
    {
        _host = host;
        InitializeComponent();

        BuildTitle();

        _demo.IsInteractive = false;
        _demo.IsHitTestVisible = false;
        DemoHost.Children.Add(_demo);

        // The flavour travels with the game, not just with the board it produced: a
        // rematch has to know whether it is repeating a setting or throwing the dice
        // again, and only the menu knows which of the three was chosen.
        LocalButton.Click += (_, _) => _host.StartGame(
            GameOptions.Hotseat(SelectedClock(), SelectedBoard(), SelectedFlavour()));
        BotButton.Click += (_, _) => _host.StartGame(GameOptions.VersusBot(
            SelectedStrength(), SelectedClock(), SelectedMovesFirst(), SelectedBoard(), SelectedFlavour()));
        SpectateButton.Click += (_, _) => _host.StartGame(
            GameOptions.Spectate(SelectedStrength(), SelectedBoard(), SelectedFlavour()));

        foreach (RadioButton option in new[] { FlavourStandardOption, FlavourRandomOption, FlavourCustomOption })
            option.Checked += (_, _) => ApplyFlavour();

        ApplyFlavour();

        SizePick.SelectionChanged += (_, _) => ApplySize();
        ApplySize();

        NetworkButton.Click += (_, _) => ShowNetwork(true);
        CloseNetworkButton.Click += (_, _) => ShowNetwork(false);
        HostButton.Click += (_, _) => StartHosting();
        JoinButton.Click += (_, _) => StartJoining();
        RulesButton.Click += (_, _) => ShowRules(true);
        CloseRulesButton.Click += (_, _) => ShowRules(false);
        QuitButton.Click += (_, _) => Application.Current.Shutdown();
        RulesOverlay.MouseLeftButtonDown += (_, _) => ShowRules(false);

        ThemeButton.Click += (_, _) => Palette.Toggle();
        SettingsButton.Click += (_, _) => ShowSettings(true);
        CloseSettingsButton.Click += (_, _) => ShowSettings(false);
        SettingsOverlay.MouseLeftButtonDown += (_, _) => ShowSettings(false);

        BuildSettings();

        // The theme can also change from Ctrl+T, which never touches this button.
        Action<AppTheme> themeHandler = _ => UpdateThemeButton();
        Palette.Changed += themeHandler;

        UpdateThemeButton();

        Loaded += (_, _) =>
        {
            Focus();
            RunEntrance();
            StartDemo();
        };

        Unloaded += (_, _) =>
        {
            Palette.Changed -= themeHandler;
            StopDemo();

            // The peer is handed to the game view once a game starts; anything still
            // sitting here was abandoned. Either way this screen stops listening to it.
            ReleasePeer(dispose: !_handedOver);
        };

        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;

            if (_settingsOpen) ShowSettings(false);
            else if (_rulesOpen) ShowRules(false);
            else return;

            e.Handled = true;
        };
    }

    /// <summary>The full engine is the default; the two heuristic bots are the handicap.</summary>
    private BotStrength SelectedStrength() => StrengthPick.SelectedIndex switch
    {
        0 => BotStrength.Easy,
        1 => BotStrength.Normal,
        _ => BotStrength.Hard,
    };

    private TimeControl SelectedClock() => ClockPick.SelectedIndex switch
    {
        1 => TimeControl.Blitz,
        2 => TimeControl.Rapid,
        _ => TimeControl.None,
    };

    private GameFlavour SelectedFlavour()
    {
        if (FlavourRandomOption.IsChecked == true) return GameFlavour.Random;
        if (FlavourCustomOption.IsChecked == true) return GameFlavour.Custom;
        return GameFlavour.Standard;
    }

    /// <summary>Whether the local player takes the first seat, rolled for a random game.</summary>
    private bool SelectedMovesFirst() => SelectedFlavour() switch
    {
        GameFlavour.Random => Random.Shared.Next(2) == 0,
        GameFlavour.Custom => OrderPick.SelectedIndex == 0,
        _ => true,
    };

    /// <summary>
    /// The board the menu currently describes. Standard is the plain game; Random rolls
    /// everything, seeded from the clock so no two are alike; Custom is whatever the
    /// dropdowns say, with a fresh seed so the same numbers still scatter differently.
    /// </summary>
    private GameSetup SelectedBoard()
    {
        // Drawn, not read off the clock: the clock only ticks every few milliseconds, so
        // two games started in quick succession were handed the same number and built the
        // same board, and a seed that climbs steadily makes them cycle rather than scatter.
        int seed = Random.Shared.Next();

        switch (SelectedFlavour())
        {
            case GameFlavour.Custom:
                int size = SelectedSize();

                // Every one of these is read off the size's own list in Core rather than a
                // copy kept here, so a number this board cannot carry is never the one sent
                // over a link — and a number offered by one build and not the other is not
                // a game that fails to start. The portals were already read that way; the
                // other three were hardcoded nines, which is how a five could be asked for
                // ten pickups and built with four.
                return new GameSetup
                {
                    Size = size,
                    Walls = Chosen(WallsPick, GameSetup.WallOptions(size)),
                    Holes = Chosen(HolesPick, GameSetup.HoleOptions(size)),
                    Pickups = Chosen(PickupsPick, GameSetup.PickupOptions(size)),
                    Portals = Chosen(PortalsPick, GameSetup.PortalOptions(size)),
                    Seed = seed,
                };

            case GameFlavour.Random:
                return GameSetup.Roll(seed);

            default:
                return GameSetup.Standard;
        }
    }

    /// <summary>Shows only what the chosen flavour is willing to be asked about.</summary>
    private void ApplyFlavour()
    {
        GameFlavour flavour = SelectedFlavour();

        CustomOptions.Visibility = flavour == GameFlavour.Custom
            ? Visibility.Visible
            : Visibility.Collapsed;

        FlavourNote.Text = flavour switch
        {
            GameFlavour.Random => "Board, walls, holes, pickups, portals and who moves first — all rolled for you.",
            GameFlavour.Custom => "Everything is yours to set.",
            _ => "Nine by nine, ten walls each. The game as it is normally played.",
        };
    }

    /// <summary>The board the size dropdown names.</summary>
    private int SelectedSize() => SizePick.SelectedIndex switch { 1 => 7, 2 => 5, _ => Board.Size };

    /// <summary>
    /// What the four setup dropdowns currently stand for. Kept because a dropdown knows
    /// only which row is selected, and a change of size has to re-choose the number that
    /// was picked rather than the row it happened to sit in.
    /// </summary>
    private int[] _wallsOffered = GameSetup.WallOptions(Board.Size);
    private int[] _holesOffered = GameSetup.HoleOptions(Board.Size);
    private int[] _pickupsOffered = GameSetup.PickupOptions(Board.Size);
    private int[] _portalsOffered = GameSetup.PortalOptions(Board.Size);

    /// <summary>The number a setup dropdown is currently naming.</summary>
    private static int Chosen(ComboBox pick, int[] offered) =>
        offered[Math.Clamp(pick.SelectedIndex, 0, offered.Length - 1)];

    /// <summary>
    /// Refits the four setup dropdowns to the chosen board, from the lists Core publishes
    /// for that size. Core keeps them precisely so that the setup screens do not each hold
    /// their own idea of what a board can carry: a number offered by one build and not the
    /// other is a game that cannot be started from a link.
    ///
    /// The nine's numbers are not a five's. A five has a quarter of the wall slots, and only
    /// 14 of its 25 squares may take a hole or a pickup at all — the two goal rows are
    /// reserved and the centre is its own mirror — so a five asked for ten pickups was built
    /// with four and said nothing. Portals are harder still: a five can carry none, because
    /// its goal rows and the rows beside them are the whole board, and a seven can carry one,
    /// because its two usable rows are a single mirrored pair and two portals sharing that
    /// pair would be one objective rather than two.
    ///
    /// A pick that survives the change keeps its number, and where the new size does not
    /// offer that number the nearest one below it is taken instead. Leaving the row where it
    /// stood would change the number under the player without saying so.
    /// </summary>
    private void ApplySize()
    {
        int size = SelectedSize();

        Offer(WallsPick, ref _wallsOffered, GameSetup.WallOptions(size),
            walls => walls.ToString());

        Offer(HolesPick, ref _holesOffered, GameSetup.HoleOptions(size),
            holes => holes == 0 ? "None" : $"{holes} — scattered at random");

        Offer(PickupsPick, ref _pickupsOffered, GameSetup.PickupOptions(size),
            pickups => pickups == 0 ? "None" : $"{pickups} — a spare wall or a free move");

        int[] offered = GameSetup.PortalOptions(size);

        Offer(PortalsPick, ref _portalsOffered, offered, portals => portals switch
        {
            0 => "None",
            1 => "1 — one pair of linked squares",
            _ => "2 — two pairs of linked squares",
        });

        PortalNote.Text = offered.Length switch
        {
            1 => "No portals on a five. A mouth may not stand on a goal row, next to one, or on the middle row — and on a five that is every row there is.",
            2 => "One portal on a seven. Its two usable rows are a single mirrored pair, and two portals sharing that pair would be one objective rather than two.",
            _ => string.Empty,
        };

        PortalNote.Visibility = PortalNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Relabels one setup dropdown to the numbers a size offers, and keeps the choice that
    /// was made across the change.
    /// </summary>
    private static void Offer(ComboBox pick, ref int[] shown, int[] offered, Func<int, string> label)
    {
        int wanted = shown[Math.Clamp(pick.SelectedIndex, 0, shown.Length - 1)];
        shown = offered;

        // The screen is built for the largest list a size can ask for, so rows are only
        // ever added if Core grows one — but a list that quietly lost its top entry here
        // would be the same divergence this whole change is undoing.
        while (pick.Items.Count < offered.Length) pick.Items.Add(new ComboBoxItem());

        // Rows this size cannot use are taken out of the list rather than merely disabled:
        // the dropdown's rows are drawn by one template with no disabled state, so a row
        // left in place would look exactly like one that can be picked.
        for (int i = 0; i < pick.Items.Count; i++)
        {
            var row = (ComboBoxItem)pick.Items[i];
            bool usable = i < offered.Length;

            if (usable) row.Content = label(offered[i]);

            row.IsEnabled = usable;
            row.Visibility = usable ? Visibility.Visible : Visibility.Collapsed;
        }

        // The number that was picked where the new size still offers it, and the nearest one
        // below it where it does not — never a bigger board's number on a smaller board.
        int index = 0;
        for (int i = 0; i < offered.Length; i++)
            if (offered[i] <= wanted)
                index = i;

        pick.SelectedIndex = index;

        // Nothing to choose between is not a choice, so the whole control goes quiet — and
        // visibly so, since the pick's own template does not draw a disabled state either.
        pick.IsEnabled = offered.Length > 1;
        pick.Opacity = offered.Length > 1 ? 1 : 0.45;
    }

    private void UpdateThemeButton() =>
        ThemeButton.Content = Palette.Current == AppTheme.Dark ? "Light theme" : "Dark theme";

    // ================================================================= title ==

    private void BuildTitle()
    {
        // Mixed case, not caps: a serif wordmark reads as a name, and letterspaced
        // capitals are the first thing that makes a title look generated.
        const string title = "Quoridor";

        foreach (char letter in title)
        {
            var glyph = new TextBlock
            {
                Text = letter.ToString(),
                Style = (Style)FindResource("Text.Display"),
                Opacity = 0,
                RenderTransform = new TranslateTransform(0, 20),
            };

            TitleHost.Children.Add(glyph);
        }
    }

    // ================================================================== demo ==

    private void StartDemo()
    {
        StopDemo();

        _demoCancellation = new CancellationTokenSource();
        _ = RunDemoAsync(_demoCancellation.Token);
    }

    private void StopDemo()
    {
        _demoCancellation?.Cancel();
        _demoCancellation?.Dispose();
        _demoCancellation = null;
    }

    /// <summary>
    /// Plays real games beside the menu, restarting when one finishes. Same board
    /// control, same engine as the game itself, so what you are watching is the actual
    /// thing rather than a canned animation — walls, jumps and all.
    /// </summary>
    private async Task RunDemoAsync(CancellationToken token)
    {
        var random = new Random();

        try
        {
            while (!token.IsCancellationRequested)
            {
                GameState state = GameState.CreateInitial();
                _demo.Reset(state);

                await Task.Delay(1000, token);

                // Two identical engines would replay one game forever, so the opening
                // few moves are random. It also gets walls onto the board early.
                int randomOpening = random.Next(2, 6);
                var history = new List<ulong>();

                for (int ply = 0; ply < 220 && !state.IsGameOver; ply++)
                {
                    Move move;

                    if (ply < randomOpening)
                    {
                        var legal = state.LegalMoves();
                        move = legal[random.Next(legal.Count)];
                    }
                    else
                    {
                        GameState snapshot = state;
                        IQuoridorAgent agent = _demoAgents[state.SideToMove];
                        agent.SetGameHistory(CollectionsMarshal.AsSpan(history));

                        // Short clock, but still a real search — it must not block the
                        // menu's own animations.
                        move = await Task.Run(() => agent.ChooseMove(snapshot, token), token);
                    }

                    history.Add(state.Hash);
                    state.Apply(move);

                    await _demo.PlayAsync(move, state);
                    await Task.Delay(430, token);
                }

                await Task.Delay(2600, token);
            }
        }
        catch (OperationCanceledException)
        {
            // The menu went away mid-game; nothing to clean up.
        }
    }

    // ============================================================== entrance ==

    private void RunEntrance()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        for (int i = 0; i < TitleHost.Children.Count; i++)
        {
            var glyph = (TextBlock)TitleHost.Children[i];
            var delay = TimeSpan.FromMilliseconds(70 + i * 48);

            glyph.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)) { BeginTime = delay });

            ((TranslateTransform)glyph.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(620)) { BeginTime = delay, EasingFunction = ease });
        }

        FadeIn(Tagline, 470);

        Divider.BeginAnimation(WidthProperty, new DoubleAnimation(0, 480, TimeSpan.FromMilliseconds(680))
        {
            BeginTime = TimeSpan.FromMilliseconds(520),
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut },
        });

        for (int i = 0; i < MenuItems.Children.Count; i++)
            SlideIn((FrameworkElement)MenuItems.Children[i], 640 + i * 72);

        SlideIn(OptionsGrid, 1000);

        DemoHost.Opacity = 0;
        DemoHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(900))
        {
            BeginTime = TimeSpan.FromMilliseconds(320),
        });
    }

    private static void FadeIn(UIElement element, double delayMs)
    {
        element.Opacity = 0;
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(520))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
        });
    }

    private static void SlideIn(FrameworkElement element, double delayMs)
    {
        var shift = new TranslateTransform(-16, 0);
        element.RenderTransform = shift;
        element.Opacity = 0;

        var delay = TimeSpan.FromMilliseconds(delayMs);

        element.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)) { BeginTime = delay });

        shift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-16, 0, TimeSpan.FromMilliseconds(560))
            {
                BeginTime = delay,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    // =============================================================== network ==

    private void StartHosting()
    {
        NetPeer peer = FreshPeer();

        // The host picks the sides for both; the other side is told on connecting.
        int seat = HostSecondOption.IsChecked == true ? 1 : 0;

        NetworkStatus.Text = $"Waiting for the other player on port {NetPeer.DefaultPort}.";

        IReadOnlyList<string> addresses = NetPeer.LocalAddresses();
        NetworkAddresses.Text = addresses.Count > 0
            ? "They should type:  " + string.Join("   or   ", addresses)
            : "No network address found — is this machine on a network?";

        _ = peer.HostAsync(NetPeer.DefaultPort, seat, SelectedBoard());
    }

    private void StartJoining()
    {
        string address = AddressBox.Text.Trim();

        if (address.Length == 0)
        {
            NetworkStatus.Text = "Type the address the host read out.";
            return;
        }

        // "1.2.3.4" or "1.2.3.4:25123" — the port is only worth typing if it was changed.
        int port = NetPeer.DefaultPort;
        int colon = address.LastIndexOf(':');

        if (colon > 0 && int.TryParse(address[(colon + 1)..], out int typed))
        {
            port = typed;
            address = address[..colon];
        }

        NetPeer peer = FreshPeer();
        NetworkStatus.Text = $"Connecting to {address}…";
        NetworkAddresses.Text = string.Empty;

        _ = peer.JoinAsync(address, port);
    }

    private NetPeer FreshPeer()
    {
        ReleasePeer(dispose: true);
        _handedOver = false;

        var peer = new NetPeer();

        // Kept rather than left anonymous so it can be taken off again. The closure holds
        // this view, and once the peer has been handed to the game the peer outlives the
        // view — see ReleasePeer.
        _peerChanged = () => Dispatcher.Invoke(() => OnPeerChanged(peer));
        peer.Changed += _peerChanged;

        _peer = peer;
        return peer;
    }

    /// <summary>
    /// Lets go of the peer this screen was holding. The subscription comes off whether or
    /// not the connection is closed with it: <see cref="NetPeer.Dispose"/> leaves its
    /// events alone, so a peer handed to the game went on holding this menu — and with it
    /// two 4 MB engine tables — for the whole of the network game, and a link that failed
    /// mid-game wrote its trouble into a status bar nobody was looking at.
    /// </summary>
    private void ReleasePeer(bool dispose)
    {
        if (_peer is null) return;

        if (_peerChanged is not null) _peer.Changed -= _peerChanged;
        _peerChanged = null;

        if (dispose) _peer.Dispose();
        _peer = null;
    }

    private void OnPeerChanged(NetPeer peer)
    {
        if (!ReferenceEquals(peer, _peer)) return;

        switch (peer.State)
        {
            case NetState.Connected:
                // Hand the live connection to the game; it owns it from here.
                _handedOver = true;
                _host.StartNetworkGame(peer);
                break;

            case NetState.Failed:
                NetworkStatus.Text = peer.Trouble;
                NetworkAddresses.Text = string.Empty;
                break;
        }
    }

    private void ShowNetwork(bool visible)
    {
        if (_networkOpen == visible) return;
        _networkOpen = visible;

        if (visible)
        {
            NetworkOverlay.Visibility = Visibility.Visible;
            NetworkStatus.Text = string.Empty;
            NetworkAddresses.Text = string.Empty;
        }
        else
        {
            ReleasePeer(dispose: true);
        }

        Fade(NetworkOverlay, NetworkScale, visible);
    }

    // ============================================================== settings ==

    private void BuildSettings()
    {
        Settings settings = Settings.Current;

        (Palette.Current == AppTheme.Dark ? ThemeDarkOption : ThemeLightOption).IsChecked = true;
        ThemeDarkOption.Checked += (_, _) => Palette.Apply(AppTheme.Dark);
        ThemeLightOption.Checked += (_, _) => Palette.Apply(AppTheme.Light);

        Choose(settings.EngineMoveTimeMs, (EngineFastOption, 400), (EngineNormalOption, 1200), (EngineDeepOption, 3000));
        Bind(value => settings.EngineMoveTimeMs = value,
            (EngineFastOption, 400), (EngineNormalOption, 1200), (EngineDeepOption, 3000));

        // Takes effect on the next game rather than this one: which engine a session
        // plays with, and how large a table it plays over, are decided when it is built.
        (settings.Ponder ? PonderOnOption : PonderOffOption).IsChecked = true;
        PonderOnOption.Checked += (_, _) => Store(() => settings.Ponder = true);
        PonderOffOption.Checked += (_, _) => Store(() => settings.Ponder = false);

        Choose(settings.WatchPaceMs, (PaceBriskOption, 500), (PaceSteadyOption, 1400), (PaceCalmOption, 2800));
        Bind(value => settings.WatchPaceMs = value,
            (PaceBriskOption, 500), (PaceSteadyOption, 1400), (PaceCalmOption, 2800));

        (settings.ShowRoutes ? RoutesShownOption : RoutesHiddenOption).IsChecked = true;
        RoutesHiddenOption.Checked += (_, _) => Store(() => settings.ShowRoutes = false);
        RoutesShownOption.Checked += (_, _) => Store(() => settings.ShowRoutes = true);

        // Volume doubles as the on/off switch: nothing to hear is what "off" means, and
        // one control is easier to reason about than a switch plus a level that disagree.
        SoundDial.Value = settings.SoundVolume;
        MusicDial.Value = settings.MusicVolume;

        SoundReading.Text = $"{settings.SoundVolume}%";
        MusicReading.Text = $"{settings.MusicVolume}%";

        SoundDial.ValueChanged += (_, e) => Store(() =>
        {
            int level = (int)Math.Round(e.NewValue);

            settings.SoundVolume = level;
            settings.Sound = level > 0;
            SoundReading.Text = $"{level}%";

            Sfx.RefreshVolumes();
            Sfx.Play(Sound.Move);
        });

        MusicDial.ValueChanged += (_, e) => Store(() =>
        {
            int level = (int)Math.Round(e.NewValue);

            settings.MusicVolume = level;
            MusicReading.Text = $"{level}%";

            bool wanted = level > 0;
            if (wanted != settings.Music)
            {
                settings.Music = wanted;
                Sfx.Music(wanted);
            }

            Sfx.RefreshVolumes();
        });

        TrackPick.SelectedIndex = Math.Clamp(settings.MusicTrack, 0, 2);
        TrackPick.SelectionChanged += (_, _) => Store(() =>
        {
            settings.MusicTrack = TrackPick.SelectedIndex;

            // Changing the piece is only audible if it is playing; start it if the
            // volume says it should be.
            if (settings.Music || settings.MusicVolume > 0)
            {
                settings.Music = true;
                Sfx.Music(true);
            }
        });

        // Picks the option matching the stored value, falling back to the middle one so
        // a hand-edited file cannot leave the group with nothing selected.
        static void Choose(int stored, params (RadioButton Option, int Value)[] choices)
        {
            foreach ((RadioButton option, int value) in choices)
            {
                if (value != stored) continue;
                option.IsChecked = true;
                return;
            }

            choices[choices.Length / 2].Option.IsChecked = true;
        }

        void Bind(Action<int> apply, params (RadioButton Option, int Value)[] choices)
        {
            foreach ((RadioButton option, int value) in choices)
                option.Checked += (_, _) => Store(() => apply(value));
        }

        static void Store(Action change)
        {
            change();
            Settings.Current.Save();
        }
    }

    private void ShowSettings(bool visible)
    {
        if (_settingsOpen == visible) return;
        _settingsOpen = visible;

        if (visible) SettingsOverlay.Visibility = Visibility.Visible;
        Fade(SettingsOverlay, SettingsScale, visible);
    }

    /// <summary>Shared entrance and exit for the three overlays this screen can raise.</summary>
    private static void Fade(UIElement overlay, ScaleTransform scale, bool visible)
    {
        var fade = new DoubleAnimation(visible ? 1 : 0, TimeSpan.FromMilliseconds(visible ? 220 : 160));
        if (!visible)
            fade.Completed += (_, _) => overlay.Visibility = Visibility.Collapsed;

        overlay.BeginAnimation(OpacityProperty, fade);

        var grow = new DoubleAnimation(visible ? 1 : 0.97, TimeSpan.FromMilliseconds(visible ? 320 : 160))
        {
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut },
        };

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    // ================================================================= rules ==

    private void ShowRules(bool visible)
    {
        if (_rulesOpen == visible) return;
        _rulesOpen = visible;

        if (visible) RulesOverlay.Visibility = Visibility.Visible;
        Fade(RulesOverlay, RulesScale, visible);
    }
}
