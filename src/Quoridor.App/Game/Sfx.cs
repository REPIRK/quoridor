using System.IO;
using System.Windows.Media;

namespace Quoridor.App.Game;

public enum Sound
{
    Move,
    Wall,
    Win,
    Lose,
}

/// <summary>
/// The game's sound, synthesised the first time it is needed and written to short WAV
/// files that WPF's MediaPlayer can open.
///
/// Nothing is shipped with the app — the download stays the single file it is, and the
/// tones are shaped here to match the board rather than picked from whatever samples were
/// to hand. Sound is a nicety, so every failure path here ends in silence rather than an
/// error: a machine with no audio device should still play Quoridor.
/// </summary>
public static class Sfx
{
    private const int Rate = 44100;

    private static readonly string Folder =
        Path.Combine(Path.GetTempPath(), "Quoridor.Sound");

    private static readonly Dictionary<Sound, MediaPlayer> Players = new();

    private static readonly object Gate = new();

    private static MediaPlayer? _music;
    private static bool _ready;

    /// <summary>
    /// Writes the sounds out ahead of time. Called at startup off the UI thread, so the
    /// first move does not pay for a dozen seconds of synthesis.
    /// </summary>
    public static void Warm()
    {
        try
        {
            Prepare();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Plays one of the effects, unless the player has turned them off.</summary>
    public static void Play(Sound sound)
    {
        if (!Settings.Current.Sound) return;

        try
        {
            Prepare();

            if (!Players.TryGetValue(sound, out MediaPlayer? player))
            {
                player = new MediaPlayer { Volume = 0.45 };
                player.Open(new Uri(PathOf(sound.ToString().ToLowerInvariant())));
                Players[sound] = player;
            }

            player.Position = TimeSpan.Zero;
            player.Play();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Starts or stops the ambient loop.</summary>
    public static void Music(bool on)
    {
        try
        {
            if (!on)
            {
                _music?.Stop();
                return;
            }

            Prepare();

            if (_music is null)
            {
                _music = new MediaPlayer { Volume = 0.30 };
                _music.Open(new Uri(PathOf("music")));

                // The loop fades in and out at its own edges, so the pause while it is
                // rewound and started again passes unnoticed.
                _music.MediaEnded += (_, _) =>
                {
                    _music.Position = TimeSpan.Zero;
                    _music.Play();
                };
            }

            _music.Position = TimeSpan.Zero;
            _music.Play();
        }
        catch (Exception)
        {
        }
    }

    private static string PathOf(string name) => Path.Combine(Folder, $"{name}.wav");

    /// <summary>
    /// Writes the files out once. Rewritten every run, so a file left half-written by a
    /// crash heals by itself. Locked because the warm-up runs off the UI thread and a
    /// move could ask for a sound while it is still going.
    /// </summary>
    private static void Prepare()
    {
        lock (Gate)
        {
            if (_ready) return;

            Directory.CreateDirectory(Folder);

            Write("move", Move());
            Write("wall", Wall());
            Write("win", Win());
            Write("lose", Lose());
            Write("music", Ambient());

            _ready = true;
        }
    }

    // ================================================================= voices ==

    private static float[] Move()
    {
        var buffer = new float[(int)(Rate * 0.10)];

        // A step: one short tone lifting a fifth, so it reads as forward motion.
        Tone(buffer, 0, 0.09, 520, 660, 0.30);
        return buffer;
    }

    private static float[] Wall()
    {
        var buffer = new float[(int)(Rate * 0.20)];

        // A wall: something wooden landing. Dull noise for the impact, a low body under it.
        Knock(buffer, 0, 0.14, 0.42);
        Tone(buffer, 0, 0.17, 180, 120, 0.26, triangle: true);
        return buffer;
    }

    private static float[] Win()
    {
        var buffer = new float[(int)(Rate * 0.85)];

        Tone(buffer, 0.00, 0.20, 523, 523, 0.26);
        Tone(buffer, 0.13, 0.20, 659, 659, 0.26);
        Tone(buffer, 0.26, 0.50, 784, 784, 0.28);
        return buffer;
    }

    private static float[] Lose()
    {
        var buffer = new float[(int)(Rate * 0.80)];

        Tone(buffer, 0.00, 0.24, 392, 392, 0.24, triangle: true);
        Tone(buffer, 0.17, 0.55, 294, 294, 0.24, triangle: true);
        return buffer;
    }

    /// <summary>
    /// Twelve seconds of pad. Every partial completes a whole number of cycles in that
    /// time and the whole thing fades at both ends, so the loop has no seam to hear.
    /// </summary>
    private static float[] Ambient()
    {
        const double Length = 12.0;

        var buffer = new float[(int)(Rate * Length)];

        // A minor ninth chord, spread wide and kept quiet: harmonically still enough to
        // ignore, which is the point of it.
        double[] partials = { 110, 165, 220, 330, 440 };
        double[] weights = { 0.34, 0.24, 0.18, 0.10, 0.06 };

        for (int p = 0; p < partials.Length; p++)
        {
            // A tremolo whose period divides the loop, so it too arrives back where it began.
            double cycles = p + 1;

            for (int i = 0; i < buffer.Length; i++)
            {
                double t = i / (double)Rate;
                double swell = 0.55 + 0.45 * Math.Sin(2 * Math.PI * cycles * t / Length);

                buffer[i] += (float)(Math.Sin(2 * Math.PI * partials[p] * t) * weights[p] * swell * 0.18);
            }
        }

        // Fade the first and last second, which is what hides the join.
        int fade = Rate;
        for (int i = 0; i < fade; i++)
        {
            double ramp = i / (double)fade;
            buffer[i] *= (float)ramp;
            buffer[^(i + 1)] *= (float)ramp;
        }

        return buffer;
    }

    // ================================================================ shaping ==

    private static void Tone(
        float[] buffer, double start, double length, double from, double to, double gain,
        bool triangle = false)
    {
        int first = (int)(start * Rate);
        int count = (int)(length * Rate);

        double phase = 0;

        for (int i = 0; i < count && first + i < buffer.Length; i++)
        {
            double progress = i / (double)count;
            double hz = from + (to - from) * progress;

            phase += 2 * Math.PI * hz / Rate;

            double wave = triangle
                ? 2 * Math.Abs(2 * (phase / (2 * Math.PI) % 1.0) - 1) - 1
                : Math.Sin(phase);

            // Quick in, slow out: the envelope of something struck.
            double attack = Math.Min(1, progress / 0.06);
            double decay = Math.Pow(1 - progress, 2.2);

            buffer[first + i] += (float)(wave * gain * attack * decay);
        }
    }

    private static void Knock(float[] buffer, double start, double length, double gain)
    {
        int first = (int)(start * Rate);
        int count = (int)(length * Rate);

        var random = new Random(7);

        // Noise through a one-pole lowpass, which is all the dullness it needs.
        double last = 0;

        for (int i = 0; i < count && first + i < buffer.Length; i++)
        {
            double noise = random.NextDouble() * 2 - 1;
            last += (noise - last) * 0.11;

            double decay = Math.Pow(1 - i / (double)count, 3);
            buffer[first + i] += (float)(last * gain * decay);
        }
    }

    // ================================================================== files ==

    private static void Write(string name, float[] samples)
    {
        using var file = new FileStream(PathOf(name), FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(file);

        int bytes = samples.Length * 2;

        writer.Write("RIFF"u8);
        writer.Write(36 + bytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);                    // PCM header length
        writer.Write((short)1);              // uncompressed
        writer.Write((short)1);              // mono
        writer.Write(Rate);
        writer.Write(Rate * 2);              // bytes per second
        writer.Write((short)2);              // bytes per frame
        writer.Write((short)16);             // bits per sample
        writer.Write("data"u8);
        writer.Write(bytes);

        foreach (float sample in samples)
        {
            double clipped = Math.Clamp(sample, -1.0, 1.0);
            writer.Write((short)(clipped * short.MaxValue));
        }
    }
}
