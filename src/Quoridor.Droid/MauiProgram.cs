using Microsoft.AspNetCore.Components.WebView.Maui;
using Quoridor.Ui.Game;

namespace Quoridor.Droid;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        // The whole reason this project exists, said in one line. The components on the
        // other side of the WebView never ask which host they are in; they ask the
        // profile how long the engine may think and whether it may think in the player's
        // time, and behind a native shell the answer is the desktop's, because the search
        // runs on a thread of its own instead of on the only thread there is.
        //
        // The invite is the one thing a native host cannot answer out of the box. The page
        // here is served from inside the app, so the address it is at is real only on this
        // phone; a player offering a game has to be able to send the other one somewhere
        // they can actually open, and that is the browser build. The code beside the link
        // is unaffected — it is the same code either way, and either side may be the phone.
        //
        // Both are set here rather than in the page because the component reads them while
        // it is being constructed, and the BlazorWebView constructs it later than this.
        HostProfile.Current = HostProfile.Native with
        {
            InviteBase = "https://repirk.github.io/quoiridor/",
        };

        // What the system's back gesture does when the page has nothing left to unwind: it
        // leaves, which is what back means at the bottom of an app. The page asks for this
        // only after having shown the player what they would be walking away from — see
        // MainPage.OnBackButtonPressed for the other half of the arrangement.
        //
        // Hopped onto the main thread rather than run where it is called from. The button
        // that asks is a component event, and a BlazorWebView dispatches those on a
        // serialised thread of its own rather than on Android's — while closing an app
        // means finishing an Activity, which is the platform's UI thread's business and
        // nobody else's.
        HostShell.Leave = () => MainThread.BeginInvokeOnMainThread(() => Application.Current?.Quit());

        MauiAppBuilder builder = MauiApp.CreateBuilder();

        builder.UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif

        TuneTheWebView();

        return builder.Build();
    }

    /// <summary>
    /// Three things the WebView will not do until it is asked.
    ///
    /// A WebView blocks audio that no one has tapped for, which the game trips over: the
    /// click of a step and the knock of a wall are made by the engine's move as often as
    /// by the player's, and the ambient loop is started from a switch in a panel rather
    /// than from the element that plays it. On every other build a sound that is turned
    /// on is a sound that is heard, and this is what that costs here.
    ///
    /// Storage is on for the same kind of reason: the page is entitled to expect a
    /// browser, and a browser that throws on the first storage call is a page that has to
    /// carry a workaround for one host. Where the settings themselves are kept is a
    /// separate question and is answered in <see cref="HostSettings"/>.
    ///
    /// And the window is handed over here because this is the first moment the view the
    /// page will be drawn in exists. What happens to it is <see cref="HostChrome"/>'s.
    /// </summary>
    private static void TuneTheWebView()
    {
        BlazorWebViewHandler.BlazorWebViewMapper.AppendToMapping(
            nameof(TuneTheWebView),
            (handler, view) =>
            {
#if ANDROID
                Android.Webkit.WebSettings? settings = handler.PlatformView.Settings;
                if (settings is null) return;

                settings.MediaPlaybackRequiresUserGesture = false;
                settings.DomStorageEnabled = true;

                HostChrome.Claim(handler.PlatformView);
#endif
            });
    }
}
