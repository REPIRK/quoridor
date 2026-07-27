using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Quoridor.App.Game;
using Quoridor.App.Theme;
using Quoridor.App.Views;

namespace Quoridor.App;

public partial class MainWindow : Window
{
    private FrameworkElement? _current;
    private WindowState _stateBeforeFullscreen = WindowState.Normal;
    private bool _isFullscreen;

    public MainWindow()
    {
        InitializeComponent();

        Palette.Changed += _ => ApplyTitleBarTheme();

        Loaded += (_, _) =>
        {
            ShowMenu();

            // Synthesising the sounds takes a moment, and none of it needs the UI thread.
            Task.Run(Sfx.Warm).ContinueWith(
                _ => { if (Settings.Current.Music) Sfx.Music(true); },
                TaskScheduler.FromCurrentSynchronizationContext());
        };
        SourceInitialized += (_, _) => ApplyTitleBarTheme();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public void ShowMenu() => Navigate(new MenuView(this));

    public void StartGame(GameOptions options) => Navigate(new GameView(this, options));

    /// <summary>Starts a game on an already-connected link. The game view owns it from here.</summary>
    public void StartNetworkGame(NetPeer peer) =>
        Navigate(new GameView(this, GameOptions.Online(peer.LocalSeat == 0, peer.Setup), peer));

    /// <summary>
    /// Cross-fades the old view out and the new one in, with a short vertical
    /// drift so the transition reads as a step forward rather than a hard cut.
    /// </summary>
    private void Navigate(FrameworkElement view)
    {
        FrameworkElement? previous = _current;
        _current = view;

        var shift = new TranslateTransform(0, 16);
        view.RenderTransform = shift;
        view.Opacity = 0;
        ViewHost.Children.Add(view);

        var enterEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        var enterDelay = TimeSpan.FromMilliseconds(previous is null ? 60 : 130);

        view.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320))
        {
            BeginTime = enterDelay,
        });

        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(420))
        {
            BeginTime = enterDelay,
            EasingFunction = enterEase,
        });

        if (previous is null)
        {
            view.Focus();
            return;
        }

        previous.IsHitTestVisible = false;

        var exitShift = new TranslateTransform();
        previous.RenderTransform = exitShift;

        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
        fadeOut.Completed += (_, _) =>
        {
            ViewHost.Children.Remove(previous);
            view.Focus();
        };

        previous.BeginAnimation(OpacityProperty, fadeOut);
        exitShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, -14, TimeSpan.FromMilliseconds(220)) { EasingFunction = enterEase });
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                break;

            case Key.T when Keyboard.Modifiers == ModifierKeys.Control:
                Palette.Toggle();
                e.Handled = true;
                break;
        }
    }

    public void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _stateBeforeFullscreen;
        }
        else
        {
            _stateBeforeFullscreen = WindowState;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;

            // Toggling through Normal forces the frame to re-measure, otherwise a
            // window that was already maximised keeps its old borders.
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }

        _isFullscreen = !_isFullscreen;
    }

    // ---------------------------------------------------------- title bar --

    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    /// <summary>Keeps the native caption in step with the in-app theme.</summary>
    private void ApplyTitleBarTheme()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        int useDark = Palette.Current == AppTheme.Dark ? 1 : 0;
        DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));
    }
}
