using Microsoft.JSInterop;

namespace Quoridor.Droid;

/// <summary>
/// Where this host keeps the panel's choices — the theme, the volumes, the confirm step,
/// the engine's budget and the game last set up — and the two methods the page reaches
/// them through.
///
/// The page asks its own storage module for them, and in a browser tab that module uses
/// localStorage. That is the right answer there and the wrong one here. Inside an app,
/// localStorage belongs to the WebView rather than to the app: it lives in the WebView's
/// own data directory, it is written by a component the system updates on its own
/// schedule, it is what "clear cache" and every storage-reclaiming sweep on the phone go
/// looking for, and nothing about it is ours to promise. A player who set the board up
/// the way they like it should find it that way next week.
///
/// So this host answers instead, and keeps them in a file in the app's private data
/// directory: the place Android gives an app for exactly this, the place it restores from
/// a backup when the phone is replaced, and the same shape of answer the desktop build
/// gives (a file it owns, written whole, read once). The page is not changed to suit it —
/// it stores the same single line it has always stored, and the storage module simply
/// hands that line to whoever the host says keeps it.
///
/// A preference is never worth a crash. Nothing here throws: an unreadable file means the
/// defaults, and an unwritable one means the choice holds for this session.
/// </summary>
public static class HostSettings
{
    /// <summary>
    /// Serialises the reads and writes. They arrive from the WebView's own thread and a
    /// write is started and not waited for, so two of them can be in flight at once.
    /// </summary>
    private static readonly object Gate = new();

    /// <summary>
    /// Named like the desktop's file rather than after the storage key the browser uses,
    /// because what it holds is the same set of preferences and someone reading a bug
    /// report should not have to be told which is which. Plain text, not JSON: what the
    /// page hands over is one line of named terms, and re-wrapping it here would mean two
    /// formats to keep in step for no gain.
    /// </summary>
    private static string Path => System.IO.Path.Combine(FileSystem.AppDataDirectory, "settings.txt");

    [JSInvokable]
    public static string ReadSettings()
    {
        try
        {
            lock (Gate) return File.Exists(Path) ? File.ReadAllText(Path) : string.Empty;
        }
        catch (Exception)
        {
            // A first run, or a file we cannot read. Both mean the defaults, which is
            // exactly what a first run is.
            return string.Empty;
        }
    }

    [JSInvokable]
    public static void WriteSettings(string text)
    {
        if (text is null) return;

        try
        {
            lock (Gate)
            {
                // Written beside the real file and then renamed over it. A rename is the
                // one file operation Android will not leave half done, and being killed
                // the moment the player closes a settings panel is ordinary on a phone in
                // a way it never is on a desktop: without this, the file that survives
                // could be the front of the new line and the tail of the old one.
                string staging = Path + ".new";

                File.WriteAllText(staging, text);
                File.Move(staging, Path, overwrite: true);
            }
        }
        catch (Exception)
        {
            // Full, or refused. The choices still hold for as long as the app is open,
            // which is the whole of what the player asked for just now.
        }
    }
}
