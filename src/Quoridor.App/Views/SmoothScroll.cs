using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Quoridor.App.Views;

/// <summary>
/// A scroll offset that can be animated.
///
/// A <see cref="ScrollViewer"/>'s own VerticalOffset is read-only and cannot be a target,
/// so a list that scrolls itself can only jump — and a jump gives no clue whether it moved
/// one row or twenty. This is the usual answer: an attached double that can be animated,
/// which passes each frame on to the viewer as it goes.
/// </summary>
internal static class SmoothScroll
{
    private static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached(
        "Offset",
        typeof(double),
        typeof(SmoothScroll),
        new PropertyMetadata(0.0, OnOffsetChanged));

    private static void OnOffsetChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is ScrollViewer viewer) viewer.ScrollToVerticalOffset((double)e.NewValue);
    }

    /// <summary>
    /// Glides <paramref name="viewer"/> to <paramref name="offset"/>. A move of less than
    /// a row is made outright: an animation that short is a flicker rather than a motion,
    /// and it is exactly what a list one line taller than its box would do on every move.
    /// </summary>
    public static void To(ScrollViewer viewer, double offset, TimeSpan duration)
    {
        offset = Math.Clamp(offset, 0, viewer.ScrollableHeight);

        // Whatever the last glide left behind is where this one starts from, so the
        // animation is dropped and its final value written back as a plain local value
        // before a new one is hung on the property.
        viewer.BeginAnimation(OffsetProperty, null);
        viewer.SetValue(OffsetProperty, viewer.VerticalOffset);

        if (Math.Abs(offset - viewer.VerticalOffset) < 4)
        {
            viewer.ScrollToVerticalOffset(offset);
            return;
        }

        viewer.BeginAnimation(OffsetProperty, new DoubleAnimation(offset, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        });
    }
}
