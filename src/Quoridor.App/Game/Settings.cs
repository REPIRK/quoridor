using System.IO;
using System.Text.Json;

namespace Quoridor.App.Game;

/// <summary>
/// Preferences that outlive a session, kept in the user's roaming profile.
///
/// Everything here is a knob someone might reasonably want at a different setting on
/// their own machine; anything the game can decide for itself is not a setting.
/// Reading or writing the file never throws — a preference is not worth a crash, so a
/// broken or missing file simply means defaults.
/// </summary>
public sealed class Settings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Quoridor",
        "settings.json");

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    /// <summary>"Dark" or "Light". Remembered so the app opens the way you left it.</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>How long the hard bot may think about one move.</summary>
    public int EngineMoveTimeMs { get; set; } = 1200;

    /// <summary>Minimum time one move takes in a watched engine game.</summary>
    public int WatchPaceMs { get; set; } = 1400;

    /// <summary>Whether the shortest routes are drawn without being asked for.</summary>
    public bool ShowRoutes { get; set; }

    public static Settings Current { get; } = Load();

    public TimeSpan EngineMoveTime => TimeSpan.FromMilliseconds(EngineMoveTimeMs);

    public TimeSpan WatchPace => TimeSpan.FromMilliseconds(WatchPaceMs);

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Format));
        }
        catch (Exception)
        {
            // A preference that cannot be written is not worth interrupting the game for.
        }
    }

    private static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception)
        {
            // Corrupt or unreadable: fall back to defaults rather than refusing to start.
        }

        return new Settings();
    }
}
