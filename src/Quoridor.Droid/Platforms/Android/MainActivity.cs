using Android.App;
using Android.Content.PM;
using Android.Views;

namespace Quoridor.Droid;

// Rotation, a fold opening, the system switching to dark: all handled by the page relaying
// itself, so the activity says it takes them rather than being torn down and rebuilt for
// each one. This is the whole of what makes turning the phone free — the game in progress
// is a live object inside the WebView, and an activity that restarts is a WebView that is
// rebuilt from its page, which is a game that is lost. It is also why the landscape rules
// in the stylesheet are enough: the page is still the same page, and it simply relays.
//
// The system killing the app in the background is the other half of the same question and
// this cannot answer it — nothing can, because there is no warning. That one is answered by
// the game being written down after every move; see HostGame.
//
// AdjustResize is asked for as the honest description of what this window wants from a
// keyboard, and on releases and configurations where it is honoured it is what happens. It
// is not what carries the invite box clear of the keyboard, though: this app is laid out
// edge to edge, and a window whose decor does not fit the system windows is a window the
// system stops resizing for a keyboard. What carries it is the keyboard's own inset, which
// HostChrome measures along with the bars and hands to the page.
[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
        ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
}
