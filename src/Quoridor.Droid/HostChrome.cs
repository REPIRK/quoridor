#if ANDROID
using Android.Views;
using AndroidX.Core.View;
#endif

using Microsoft.JSInterop;

namespace Quoridor.Droid;

/// <summary>
/// The window around the page: the status bar at the top, whatever the phone keeps at the
/// bottom, and how much of the screen either of them is standing on.
///
/// This app is laid out under both of them on purpose. Android 15 makes that the default
/// and this build agrees with it rather than opting out: a board drawn to the edges of the
/// glass, with the page's own ink running up behind the clock and down behind the gesture
/// bar, is the version of this game that looks like it belongs on a phone. The price is
/// that the page has to be told where those bars are, because content laid under a bar is
/// content that has to keep out from under it — and that is the whole of what this class
/// does.
///
/// Two directions, and both of them are needed.
///
/// Outwards: the bars are painted by the system and the theme is remembered by the page, so
/// the system cannot know whether it is drawing a clock over a dark board or a light one.
/// The page says, and the icons are set light or dark to match. The bars themselves are
/// left transparent, so what shows through them is the page's own background — which is why
/// they follow the theme exactly and cannot drift from it.
///
/// Inwards: how tall the bars are is not something CSS can find out on Android. The web's
/// own answer, env(safe-area-inset-*), reports the display cutout and nothing else — a
/// notch, yes; the status bar and the navigation bar, no — so a page relying on it alone
/// would put its header under the clock on every phone that has no notch. The insets are
/// measured natively and handed to the page as custom properties, which the stylesheet
/// takes the larger of against env(). See the top of app.css.
///
/// Nothing here throws. A phone that will not answer one of these questions is a phone that
/// gets a board drawn slightly further from an edge than it needed to be.
/// </summary>
public static class HostChrome
{
    /// <summary>
    /// The WebView the page is in, so the insets can be pushed to it as they change. Held
    /// statically because the thing that measures them is a listener on the view itself
    /// and the thing that asks for them again is a call arriving from inside the page.
    /// </summary>
    private static Android.Webkit.WebView? _view;

    /// <summary>
    /// The last insets measured, in CSS pixels. Kept as well as pushed: the first ones
    /// arrive while the page is still starting and a push into a page with no script yet
    /// is a push into nothing, so the page pulls this once when it first has something to
    /// say — see <see cref="SetTheme"/>.
    /// </summary>
    private static string _insets = "0,0,0,0";

    /// <summary>
    /// Takes over the window: bars laid under, painted by nobody, and a listener that
    /// reports how much of the screen they are standing on. Called once, with the WebView
    /// the page will be drawn in.
    /// </summary>
    public static void Claim(Android.Webkit.WebView view)
    {
#if ANDROID
        _view = view;

        try
        {
            // Said in full: this project has a Window of its own from MAUI, and the one the
            // system bars belong to is the platform's.
            Android.Views.Window? window = Platform.CurrentActivity?.Window;
            if (window is null) return;

            // The page is laid out edge to edge, and what the bars are over is the page.
            WindowCompat.SetDecorFitsSystemWindows(window, false);

            // From Android 15 the bars are transparent and cannot be otherwise; asking is
            // not merely unnecessary there, it is a call the platform has withdrawn. Below
            // it they default to the theme's own dark, which would put an opaque strip over
            // a page that is drawing underneath it — so on those releases it is asked for.
            if (!OperatingSystem.IsAndroidVersionAtLeast(35))
            {
#pragma warning disable CA1422
                window.SetStatusBarColor(Android.Graphics.Color.Transparent);
                window.SetNavigationBarColor(Android.Graphics.Color.Transparent);
#pragma warning restore CA1422
            }

            ViewCompat.SetOnApplyWindowInsetsListener(view, new Measurer());
            ViewCompat.RequestApplyInsets(view);
        }
        catch (Exception)
        {
            // An unusual window, or a platform that will not be told. The page then sees
            // no insets and draws itself as a browser tab would, which is the layout it
            // has always had.
        }
#endif
    }

    /// <summary>
    /// Which way the page's theme has gone, and the ink it meets the system's bars with.
    /// Called by the page whenever the theme changes, and once when it first starts — which
    /// is also the moment the insets measured before there was a page to tell are handed
    /// over.
    /// </summary>
    [JSInvokable]
    public static void SetTheme(bool light, string ink)
    {
#if ANDROID
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                // Said in full: this project has a Window of its own from MAUI, and the one
                // the system bars belong to is the platform's.
                Android.Views.Window? window = Platform.CurrentActivity?.Window;

                Android.Views.View? decor = window?.DecorView;
                if (window is null || decor is null) return;

                // Icons, not colour. The bars stay transparent and the colour under them is
                // the page's own, so the only thing the system still has to decide is
                // whether its clock and its gesture bar are drawn dark or light — which is
                // decided by what is behind them, which is what has just changed.
                WindowInsetsControllerCompat? controller = WindowCompat.GetInsetsController(window, decor);
                if (controller is null) return;

                controller.AppearanceLightStatusBars = light;
                controller.AppearanceLightNavigationBars = light;

                // And the window behind everything, which is what shows for the instant
                // between the splash going and the page painting.
                decor.SetBackgroundColor(Android.Graphics.Color.ParseColor(ink));
            }
            catch (Exception)
            {
                // The bars keep whatever appearance they had.
            }

            Push();
        });
#endif
    }

    /// <summary>Hands the page the insets as they now stand.</summary>
    private static void Push()
    {
#if ANDROID
        Android.Webkit.WebView? view = _view;
        if (view is null) return;

        string[] sides = _insets.Split(',');
        if (sides.Length != 4) return;

        try
        {
            // Set on the root element, which is where the stylesheet reads them from. Four
            // separate properties rather than one shorthand, because each is read on its
            // own by a max() against the web's own answer for the same side.
            view.EvaluateJavascript(
                "(function(s){" +
                "s.setProperty('--inset-top','" + sides[0] + "px');" +
                "s.setProperty('--inset-right','" + sides[1] + "px');" +
                "s.setProperty('--inset-bottom','" + sides[2] + "px');" +
                "s.setProperty('--inset-left','" + sides[3] + "px');" +
                "})(document.documentElement.style)",
                null);
        }
        catch (Exception)
        {
            // The page is not there to be told, or is on its way out.
        }
#endif
    }

#if ANDROID
    /// <summary>
    /// Measures what the page is laid out under, every time it changes — which is a
    /// rotation, a fold opening, the gesture bar swapping for three buttons, and the
    /// keyboard coming up.
    /// </summary>
    private sealed class Measurer : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat? OnApplyWindowInsets(Android.Views.View? view, WindowInsetsCompat? insets)
        {
            if (view is null || insets is null) return insets;

            try
            {
                // Three kinds together, and GetInsets takes the largest on each side. The
                // bars are what is normally in the way; the cutout is the notch or the
                // punched hole, which in landscape is on a long edge and is the one thing
                // the web's own answer would have caught on its own.
                //
                // And the keyboard, which is the one this window would otherwise be told
                // about and do nothing with. AdjustResize is what shrinks a window for a
                // keyboard, and it is exactly what laying out edge to edge switches off:
                // once the decor no longer fits the system windows, the window keeps its
                // full height and the app is expected to keep out of the keyboard's way
                // itself. The invite code is the only thing on this screen that is typed
                // into, and it is the one that would have been typed into from under a
                // keyboard. Zero whenever there is no keyboard, so nothing else changes.
                AndroidX.Core.Graphics.Insets? room = insets.GetInsets(
                    WindowInsetsCompat.Type.SystemBars()
                    | WindowInsetsCompat.Type.DisplayCutout()
                    | WindowInsetsCompat.Type.Ime());

                if (room is null) return insets;

                // Android measures in device pixels and CSS is in reference pixels, and the
                // ratio between them is exactly the display density.
                Android.Util.DisplayMetrics? metrics = view.Resources?.DisplayMetrics;
                float density = metrics is null ? 1f : metrics.Density;
                if (density <= 0) density = 1f;

                _insets = string.Join(',', new[]
                {
                    Css(room.Top, density), Css(room.Right, density),
                    Css(room.Bottom, density), Css(room.Left, density),
                });

                Push();
            }
            catch (Exception)
            {
                // Leave the page with whatever it was last told.
            }

            // Handed on rather than consumed: nothing else in this window wants them, but
            // swallowing insets is the kind of thing that goes wrong two layers away.
            return insets;
        }

        private static string Css(int pixels, float density) =>
            (pixels / density).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }
#endif
}
