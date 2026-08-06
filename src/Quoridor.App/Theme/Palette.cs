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
    /// The darkest tone the board has: what you see at the bottom of a square that has been
    /// taken out of play. A square out of play is an opening rather than an outlined square —
    /// an outline reads as a selection, and something you can see down into reads as a hole.
    /// </summary>
    public const string Pit = "Brush.Pit";

    /// <summary>
    /// The far wall of that opening, which is the part of it the lamp still reaches. The
    /// near wall is in its own shadow and gets <see cref="Pit"/>, so one gradient between
    /// the two says which way you are looking down past the board's surface.
    /// </summary>
    public const string PitLit = "Brush.PitLit";

    /// <summary>
    /// The tray the board is set in. The rim is the raised part and so the nearest thing to
    /// the lamp, which is why it is the lighter of the two tones; the field is the recess the
    /// tiles are set into, and the dark that collects under an edge you are looking down past
    /// is <see cref="FieldDeep"/> — a narrow band directly beneath that edge rather than a
    /// wash down the whole of it.
    /// </summary>
    public const string RimTop = "Brush.RimTop";

    public const string RimBottom = "Brush.RimBottom";
    public const string Field = "Brush.Field";
    public const string FieldDeep = "Brush.FieldDeep";

    /// <summary>
    /// A tile catches the light along its top edge and loses it along its bottom one. The
    /// body between the two is <see cref="Cell"/>, so the three tones cannot disagree about
    /// what colour the board is; and the bevel is the tile's own fill rather than a second
    /// shape laid on it, which is what lets eighty-one squares be bevelled by one brush.
    /// </summary>
    public const string TileLit = "Brush.TileLit";

    public const string TileShade = "Brush.TileShade";

    /// <summary>The hairline around a square, so the grid has edges rather than only gaps.</summary>
    public const string CellEdge = "Brush.CellEdge";

    /// <summary>
    /// The lamp, which is light in the room rather than paint on the board — which is why it
    /// is held apart from every colour above. <see cref="Lit"/> and <see cref="Shade"/> are
    /// the two ends of it, <see cref="LitEdge"/> is the same light caught along the top edge
    /// of a piece that stands out of the board, and <see cref="Shadow"/> is what such a piece
    /// throws on the squares beside it.
    /// </summary>
    public const string Lit = "Brush.Lit";

    public const string LitEdge = "Brush.LitEdge";
    public const string Shade = "Brush.Shade";
    public const string Shadow = "Brush.Shadow";

    /// <summary>
    /// The side face of a piece in a player's colour: that same colour with the light off
    /// it. A wall standing proud of its groove shows both faces at once, and the difference
    /// between them is the whole of why it reads as solid rather than as printed on.
    /// </summary>
    public const string Accent0Side = "Brush.Accent0Side";

    public const string Accent1Side = "Brush.Accent1Side";

    /// <summary>
    /// The seam between the two players' reaches. Deliberately neither player's ink: the
    /// line is not a piece belonging to a side, it is the shape the two sides make.
    /// </summary>
    public const string Front = "Brush.Front";

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
        [Cell] = (Rgb(0x26, 0x24, 0x20), Rgb(0xED, 0xE8, 0xDD)),
        [Line] = (Rgb(0x35, 0x32, 0x2C), Rgb(0xC9, 0xC0, 0xAD)),
        [Text] = (Rgb(0xED, 0xE7, 0xDA), Rgb(0x1C, 0x1A, 0x17)),
        [Muted] = (Rgb(0x8B, 0x83, 0x75), Rgb(0x6B, 0x63, 0x57)),
        [Accent0] = (Rgb(0x6B, 0xAA, 0xA6), Rgb(0x2D, 0x5B, 0x5D)),
        [Accent1] = (Rgb(0xD9, 0x6C, 0x4A), Rgb(0xA6, 0x43, 0x2A)),
        [Wall] = (Rgb(0xE6, 0xDF, 0xD1), Rgb(0x2A, 0x26, 0x22)),
        [Danger] = (Rgb(0xD9, 0x55, 0x3A), Rgb(0x8E, 0x2B, 0x18)),
        [Overlay] = (Argb(0xE8, 0x17, 0x17, 0x14), Argb(0xE8, 0xE8, 0xE3, 0xD8)),
        [Pit] = (Rgb(0x0A, 0x09, 0x08), Rgb(0x8B, 0x81, 0x70)),
        [Front] = (Rgb(0xB8, 0xAB, 0x90), Rgb(0x6F, 0x65, 0x52)),

        // The lit board. Every value below is the one the shared stylesheet already uses
        // for the same thing, because the two front ends are meant to be one board seen
        // twice rather than two boards that happen to agree.
        [PitLit] = (Rgb(0x1E, 0x1B, 0x17), Rgb(0xA4, 0x9A, 0x86)),
        [RimTop] = (Rgb(0x2B, 0x28, 0x23), Rgb(0xE8, 0xE2, 0xD3)),
        [RimBottom] = (Rgb(0x1E, 0x1C, 0x18), Rgb(0xD2, 0xCB, 0xBA)),
        [Field] = (Rgb(0x13, 0x12, 0x10), Rgb(0xC9, 0xC1, 0xAE)),
        [FieldDeep] = (Rgb(0x0B, 0x0A, 0x09), Rgb(0xB5, 0xAC, 0x96)),
        [TileLit] = (Rgb(0x32, 0x2F, 0x28), Rgb(0xF8, 0xF5, 0xED)),
        [TileShade] = (Rgb(0x1E, 0x1C, 0x19), Rgb(0xDC, 0xD5, 0xC5)),
        [CellEdge] = (Rgb(0x2F, 0x2C, 0x27), Rgb(0xD3, 0xCB, 0xBA)),
        [Lit] = (Rgb(0xFF, 0xF6, 0xE6), Rgb(0xFF, 0xFF, 0xFF)),

        // The edge is the lamp at a fixed fraction of itself, so the fraction is carried in
        // the alpha rather than in an Opacity on the element. A shared brush follows the
        // theme by itself; an element's opacity would have to be found again and reset, and
        // every wall already on the board would keep the old palette's light on its top face.
        [LitEdge] = (Argb(0x38, 0xFF, 0xF6, 0xE6), Argb(0x38, 0xFF, 0xFF, 0xFF)),

        // A shadow on warm paper is a warm brown. Pure black in the light theme is the one
        // thing that would make the whole board look like a mistake.
        [Shade] = (Rgb(0x00, 0x00, 0x00), Rgb(0x4A, 0x3F, 0x2C)),
        [Shadow] = (Rgb(0x07, 0x06, 0x05), Rgb(0x5D, 0x53, 0x40)),
        [Accent0Side] = (Rgb(0x3C, 0x6C, 0x6A), Rgb(0x17, 0x38, 0x3A)),
        [Accent1Side] = (Rgb(0x8B, 0x3E, 0x2B), Rgb(0x67, 0x28, 0x1A)),
    };

    private static readonly Dictionary<string, SolidColorBrush> Brushes = new();

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    /// <summary>
    /// How much light the room has, as the strength of the two ends of the lamp laid across
    /// the board. It cannot be a colour: the same white at the same strength is a faint haze
    /// on a dark board and a wash that bleaches a pale one, so how far the lamp is turned up
    /// is a property of the theme and lives here beside the ink it is made of.
    /// </summary>
    public static double LitStrength => Current == AppTheme.Dark ? 0.055 : 0.34;

    public static double ShadeStrength => Current == AppTheme.Dark ? 0.16 : 0.12;

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

    /// <summary>
    /// One colour laid over another by <paramref name="amount"/> (0 to 1), mixed in
    /// linear light rather than in the encoded values.
    ///
    /// sRGB is not linear, so the average of two sRGB numbers is darker than the average
    /// of the two lights they stand for — the classic dip in the middle of a gradient.
    /// The board mixes a tile toward a player's ink to say whose reach that square is in,
    /// and a mix that lost brightness on the way would read as a shadow falling across
    /// the board rather than as a stain in it.
    /// </summary>
    public static Color Mix(Color under, Color over, double amount)
    {
        double t = Math.Clamp(amount, 0, 1);

        return Color.FromRgb(
            Channel(under.R, over.R),
            Channel(under.G, over.G),
            Channel(under.B, over.B));

        byte Channel(byte a, byte b) => ToSrgb(ToLinear(a) + (ToLinear(b) - ToLinear(a)) * t);

        static double ToLinear(byte value)
        {
            double c = value / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        static byte ToSrgb(double linear)
        {
            double c = Math.Clamp(linear, 0, 1);
            double encoded = c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
            return (byte)Math.Round(encoded * 255);
        }
    }

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
