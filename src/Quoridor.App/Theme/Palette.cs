using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Quoridor.App.Theme;

public enum AppTheme
{
    Dark,
    Light,
}

/// <summary>
/// The single source of colour for the whole app.
///
/// Every brush is created once and shared, and styles reference them through
/// <c>DynamicResource</c>. Switching theme therefore does not rebuild any visual
/// tree — it animates the <see cref="SolidColorBrush.Color"/> of the shared brush
/// instances, so the entire window cross-fades in one pass.
/// </summary>
public static class Palette
{
    public const string Bg = "Brush.Bg";
    public const string Surface = "Brush.Surface";
    public const string BoardSurface = "Brush.Board";
    public const string Cell = "Brush.Cell";
    public const string Line = "Brush.Line";
    public const string Text = "Brush.Text";
    public const string Muted = "Brush.Muted";
    public const string Accent0 = "Brush.Accent0";
    public const string Accent1 = "Brush.Accent1";
    public const string Wall = "Brush.Wall";
    public const string Danger = "Brush.Danger";
    public const string Overlay = "Brush.Overlay";

    /// <summary>
    /// Ink on paper. The light theme is a warm bone sheet with printer's ink; the dark
    /// theme is the same ink well with the paper taken away, so the greys stay warm
    /// rather than sliding into the blue-black that every dark UI defaults to.
    ///
    /// The two players are teal and vermilion — far enough apart to read instantly,
    /// both muted enough to sit on paper without shouting.
    /// </summary>
    private static readonly Dictionary<string, (Color Dark, Color Light)> Definitions = new()
    {
        [Bg] = (Rgb(0x17, 0x17, 0x14), Rgb(0xE8, 0xE3, 0xD8)),
        [Surface] = (Rgb(0x1F, 0x1E, 0x1B), Rgb(0xF4, 0xF1, 0xE9)),
        [BoardSurface] = (Rgb(0x1C, 0x1B, 0x18), Rgb(0xDC, 0xD5, 0xC6)),
        [Cell] = (Rgb(0x26, 0x24, 0x20), Rgb(0xED, 0xE8, 0xDD)),
        [Line] = (Rgb(0x35, 0x32, 0x2C), Rgb(0xC9, 0xC0, 0xAD)),
        [Text] = (Rgb(0xED, 0xE7, 0xDA), Rgb(0x1C, 0x1A, 0x17)),
        [Muted] = (Rgb(0x8B, 0x83, 0x75), Rgb(0x6B, 0x63, 0x57)),
        [Accent0] = (Rgb(0x6B, 0xAA, 0xA6), Rgb(0x2D, 0x5B, 0x5D)),
        [Accent1] = (Rgb(0xD9, 0x6C, 0x4A), Rgb(0xA6, 0x43, 0x2A)),
        [Wall] = (Rgb(0xE6, 0xDF, 0xD1), Rgb(0x2A, 0x26, 0x22)),
        [Danger] = (Rgb(0xD9, 0x55, 0x3A), Rgb(0x8E, 0x2B, 0x18)),
        [Overlay] = (Argb(0xE8, 0x17, 0x17, 0x14), Argb(0xE8, 0xE8, 0xE3, 0xD8)),
    };

    private static readonly Dictionary<string, SolidColorBrush> Brushes = new();

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    /// <summary>Raised after a theme switch starts, so owners of raw <see cref="Color"/> values can follow.</summary>
    public static event Action<AppTheme>? Changed;

    public static void Install(ResourceDictionary resources, AppTheme initial = AppTheme.Dark)
    {
        Current = initial;

        foreach ((string key, (Color Dark, Color Light) pair) in Definitions)
        {
            Color colour = Current == AppTheme.Dark ? pair.Dark : pair.Light;
            var brush = new SolidColorBrush(colour);

            // Anything freezable that lands in Application.Resources gets frozen by
            // WPF, and a frozen brush can never be animated again. A held zero-length
            // animation leaves an animation clock attached, which makes CanFreeze
            // false and keeps the brush editable for the rest of the session.
            Hold(brush, colour, TimeSpan.Zero);

            Brushes[key] = brush;
            resources[key] = brush;
        }
    }

    private static void Hold(SolidColorBrush brush, Color target, TimeSpan duration, IEasingFunction? easing = null)
    {
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(target, new Duration(duration))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd,
        });
    }

    public static Color ColorOf(string key)
    {
        (Color dark, Color light) = Definitions[key];
        return Current == AppTheme.Dark ? dark : light;
    }

    public static SolidColorBrush BrushOf(string key) => Brushes[key];

    public static void Apply(AppTheme theme, bool animate = true)
    {
        if (theme == Current && Brushes.Count > 0) return;

        Current = theme;

        TimeSpan duration = TimeSpan.FromMilliseconds(animate ? 420 : 0);
        var easing = animate ? new CubicEase { EasingMode = EasingMode.EaseInOut } : null;

        foreach ((string key, SolidColorBrush brush) in Brushes)
            Hold(brush, ColorOf(key), duration, easing);

        Changed?.Invoke(theme);
    }

    public static AppTheme Toggle()
    {
        AppTheme next = Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        Apply(next);
        return next;
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static Color Argb(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);
}
