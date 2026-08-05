namespace Quoridor.Ui.Game;

/// <summary>
/// The two things a native shell and the page inside it have to say to each other, and the
/// only two. <see cref="HostProfile"/> answers what the host can do; this is what the host
/// and the page do to each other while it is running.
///
/// Both are plain delegates and not JavaScript. The phone build is one process: the shell
/// and the components are the same .NET runtime, and a back press arriving at an activity
/// is already on the same thread the renderer dispatches on. Going out through the WebView
/// and back in again would add a round trip, a second format to keep in step, and — for the
/// back press, which has to be answered before the frame it arrived in ends — an answer
/// that comes too late to be one.
///
/// A browser tab sets neither and calls neither. Nothing here is reached in a build with no
/// shell around it, which is why the page never asks which host it is in.
/// </summary>
public static class HostShell
{
    /// <summary>
    /// What the page does about the system's back gesture, if anything.
    ///
    /// The page answers true when it took the press and false when it had nothing to unwind,
    /// which is the shell's cue to do whatever back means where there is no page — leaving
    /// the app. It is a question and not a notification for exactly that reason.
    ///
    /// Called on the shell's own thread, which is not the renderer's: a BlazorWebView
    /// serialises the renderer onto a thread of its own, and a back press arrives on the
    /// platform's UI thread. So this reads the page's state to decide — a read that can at
    /// worst be one frame stale, and a frame ago is where the player's eyes were when they
    /// swiped — and changes nothing itself. Whatever it changes it changes through the
    /// renderer, which is the only thread allowed to.
    /// </summary>
    public static Func<bool>? BackPressed { get; set; }

    /// <summary>
    /// Closes the app, where there is an app to close. The page asks for this only after a
    /// player has been shown what they would be abandoning and has said yes; a browser tab
    /// leaves it null, and the card that would have called it is never reached there because
    /// nothing in a tab asks the page to leave.
    /// </summary>
    public static Action? Leave { get; set; }
}
