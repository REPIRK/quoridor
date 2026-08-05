using Microsoft.JSInterop;

namespace Quoridor.Droid;

/// <summary>
/// Where the game in progress is kept between one run of the app and the next.
///
/// A separate file from the settings, and separately answered, because the two are not the
/// same kind of thing. The settings are a handful of choices written when somebody makes
/// one; this is written after every move, it is empty far more often than not — a game that
/// ends leaves nothing behind — and it is the one thing on this phone whose loss a player
/// would actually notice, because it is the game they were in the middle of.
///
/// It matters more here than anywhere else the page runs. A browser tab is closed by the
/// person using it; an app on a phone is forgotten by the system, without warning, while
/// the player is answering a message, and there is no callback that says so in time to
/// write anything down. Nothing is written at the moment of leaving for that reason: what
/// survives is what was already on disk, so being killed and being closed and running out
/// of battery all come back to the same board.
///
/// The same file-then-rename as the settings, and for a sharper version of the same reason:
/// this is written forty times a game rather than twice a month, so the window in which a
/// kill could catch a half-written file is forty times wider.
/// </summary>
public static class HostGame
{
    private static readonly object Gate = new();

    private static string Path => System.IO.Path.Combine(FileSystem.AppDataDirectory, "game.txt");

    [JSInvokable]
    public static string ReadGame()
    {
        try
        {
            lock (Gate) return File.Exists(Path) ? File.ReadAllText(Path) : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    [JSInvokable]
    public static void WriteGame(string text)
    {
        try
        {
            lock (Gate)
            {
                // A game that is over, or one that was never resumable. Deleted rather than
                // written empty: an app whose data directory holds nothing is the honest
                // description of an app with no game in progress, and a stale file is the
                // one thing that could put a finished game back on the board.
                if (string.IsNullOrEmpty(text))
                {
                    if (File.Exists(Path)) File.Delete(Path);
                    return;
                }

                string staging = Path + ".new";

                File.WriteAllText(staging, text);
                File.Move(staging, Path, overwrite: true);
            }
        }
        catch (Exception)
        {
            // Full, or refused. The game on the screen is unaffected; only coming back to
            // it later is.
        }
    }
}
