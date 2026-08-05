using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quoridor.Session;

/// <summary>
/// Preferences that outlive a session.
///
/// Everything here is a knob someone might reasonably want at a different setting on
/// their own machine; anything the game can decide for itself is not a setting.
/// Reading or writing the file never throws — a preference is not worth a crash, so a
/// broken or missing file simply means defaults.
///
/// Where the file lives is the host's business, not this class's: a roaming profile is
/// the right answer on Windows and there is no such thing on a phone. The host names the
/// file once at startup with <see cref="UseFile"/>; until it does, and if it never does,
/// the preferences are simply the defaults and saving them does nothing.
/// </summary>
public sealed class Settings
{
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    /// <summary>
    /// Guards the one-time load. Every caller today is on its own UI thread, but a
    /// lazily built singleton that is only safe by accident is not worth the doubt.
    /// </summary>
    private static readonly object Gate = new();

    private static string? _filePath;

    private static Settings? _current;

    /// <summary>"Dark" or "Light". Remembered so the app opens the way you left it.</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>How long the hard bot may think about one move.</summary>
    public int EngineMoveTimeMs { get; set; } = 1200;

    /// <summary>
    /// Whether the engine keeps searching while you are deciding. Its answer comes back
    /// no slower either way — the thinking happens in time that would otherwise be spent
    /// idle — but it costs a processor core for as long as you take, which on a laptop
    /// is battery. That is a cost the game cannot see, so it is a switch rather than a
    /// decision made for you.
    /// </summary>
    public bool Ponder { get; set; } = true;

    /// <summary>Minimum time one move takes in a watched engine game.</summary>
    public int WatchPaceMs { get; set; } = 1400;

    /// <summary>Whether the shortest routes are drawn without being asked for.</summary>
    public bool ShowRoutes { get; set; }

    /// <summary>The click of a step and the knock of a wall. On, because they are brief.</summary>
    public bool Sound { get; set; } = true;

    /// <summary>The ambient loop. Off, because that is a matter of taste.</summary>
    public bool Music { get; set; }

    /// <summary>Loudness of the effects and of the music, each 0 to 100.</summary>
    public int SoundVolume { get; set; } = 75;

    public int MusicVolume { get; set; } = 55;

    /// <summary>Which of the three pieces plays: 0 Ink, 1 Slate, 2 Glass.</summary>
    public int MusicTrack { get; set; }

    /// <summary>
    /// Tells the preferences where they are kept. Call it before anything reads
    /// <see cref="Current"/> — afterwards the loaded settings are already the ones in
    /// hand, and pointing them at another file would leave the game showing one set and
    /// writing another.
    /// </summary>
    public static void UseFile(string path)
    {
        lock (Gate) _filePath = path;
    }

    public static Settings Current
    {
        get { lock (Gate) return _current ??= Load(); }
    }

    // Derived from the two above, so they are written out only to be ignored on the way
    // back in. Kept out of the file to save anyone reading it the confusion.
    [JsonIgnore]
    public TimeSpan EngineMoveTime => TimeSpan.FromMilliseconds(EngineMoveTimeMs);

    [JsonIgnore]
    public TimeSpan WatchPace => TimeSpan.FromMilliseconds(WatchPaceMs);

    public void Save()
    {
        string? path;
        lock (Gate) path = _filePath;

        // A host that never named a file is one that keeps its preferences somewhere else,
        // or nowhere; either way there is nothing here to write.
        if (path is null) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Format));
        }
        catch (Exception)
        {
            // A preference that cannot be written is not worth interrupting the game for.
        }
    }

    // Called under the lock, so it reads the file path directly.
    private static Settings Load()
    {
        try
        {
            if (_filePath is not null && File.Exists(_filePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(_filePath)) ?? new Settings();
        }
        catch (Exception)
        {
            // Corrupt or unreadable: fall back to defaults rather than refusing to start.
        }

        return new Settings();
    }
}
