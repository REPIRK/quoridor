namespace Quoridor.Session;

/// <summary>
/// A chess clock: a starting budget per player plus an increment credited after each
/// move. The increment is what keeps a timed game from turning into a scramble in the
/// last thirty seconds.
/// </summary>
public sealed record TimeControl(TimeSpan Initial, TimeSpan Increment, string Label)
{
    public static readonly TimeControl None = new(TimeSpan.Zero, TimeSpan.Zero, "Off");

    public static readonly TimeControl Blitz = new(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(3), "5+3");

    public static readonly TimeControl Rapid = new(TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(2), "3+2");

    public bool IsEnabled => Initial > TimeSpan.Zero;

    /// <summary>Formats a remaining budget the way a clock would show it.</summary>
    public static string Format(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

        // Under ten seconds the tenths are the only thing worth looking at.
        return remaining < TimeSpan.FromSeconds(10)
            ? $"{remaining.TotalSeconds:0.0}"
            : $"{(int)remaining.TotalMinutes}:{remaining.Seconds:00}";
    }
}
