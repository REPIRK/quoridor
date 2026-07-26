using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Quoridor.App.Theme;
using Quoridor.Core;

namespace Quoridor.App.Controls;

/// <summary>
/// One player's standing: name, walls still in hand, distance to their goal row, and
/// whose turn it is.
///
/// Not a card — a block set against a rule, the way an entry sits in a table of
/// contents. Boxing it would say "widget"; this says "line item". The walls are drawn
/// as the little uprights they are, standing in a rack and going out as they are spent.
/// </summary>
public sealed class PlayerCard : UserControl
{
    private const int WallBarWidth = 3;
    private const int WallBarHeight = 15;

    private readonly string _accentKey;
    private readonly Rectangle _turnMark;
    private readonly TextBlock _name;
    private readonly TextBlock _steps;
    private readonly TextBlock _wallCount;
    private readonly TextBlock _clock;
    private readonly Rectangle[] _walls = new Rectangle[Board.WallsPerPlayer];

    private int _lastWalls = Board.WallsPerPlayer;
    private bool _lastActive;

    public PlayerCard(string accentKey)
    {
        _accentKey = accentKey;

        _turnMark = new Rectangle
        {
            Width = 2,
            Height = 0,
            Fill = Palette.BrushOf(accentKey),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        _name = new TextBlock
        {
            FontFamily = new FontFamily("Sitka Display, Georgia"),
            FontSize = 19,
            Foreground = Palette.BrushOf(Palette.Text),
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        _steps = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = Palette.BrushOf(Palette.Muted),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        _clock = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 20,
            Foreground = Palette.BrushOf(Palette.Text),
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 0, 1),
            Visibility = Visibility.Collapsed,
        };

        _wallCount = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = Palette.BrushOf(Palette.Muted),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };

        var rack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        for (int i = 0; i < _walls.Length; i++)
        {
            var upright = new Rectangle
            {
                Width = WallBarWidth,
                Height = WallBarHeight,
                Margin = new Thickness(0, 0, 4, 0),
                Fill = Palette.BrushOf(accentKey),
            };

            _walls[i] = upright;
            rack.Children.Add(upright);
        }

        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_name, 0);
        Grid.SetColumn(_clock, 1);
        header.Children.Add(_name);
        header.Children.Add(_clock);

        var wallRow = new StackPanel { Orientation = Orientation.Horizontal };
        wallRow.Children.Add(rack);
        wallRow.Children.Add(_wallCount);

        var bottom = new Grid();
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(wallRow, 0);
        Grid.SetColumn(_steps, 1);
        bottom.Children.Add(wallRow);
        bottom.Children.Add(_steps);

        var body = new StackPanel { Margin = new Thickness(18, 0, 0, 0) };
        body.Children.Add(header);
        body.Children.Add(bottom);

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_turnMark, 0);
        Grid.SetColumn(body, 1);
        layout.Children.Add(_turnMark);
        layout.Children.Add(body);

        Content = layout;
        Padding = new Thickness(0, 4, 0, 4);
    }

    public void Update(string name, int wallsLeft, int distance, bool active, string? clock = null)
    {
        _name.Text = name;
        _wallCount.Text = wallsLeft.ToString();
        _steps.Text = distance < 0 ? "no route" : distance == 0 ? "home" : $"{distance} to go";

        if (clock is null)
        {
            _clock.Visibility = Visibility.Collapsed;
        }
        else
        {
            _clock.Visibility = Visibility.Visible;
            _clock.Text = clock;

            // Under ten seconds the clock stops being information and starts being
            // the thing you are playing against.
            _clock.Foreground = clock.Contains('.')
                ? Palette.BrushOf(Palette.Danger)
                : Palette.BrushOf(Palette.Text);
        }

        for (int i = 0; i < _walls.Length; i++)
        {
            double target = i < wallsLeft ? 1 : 0.16;

            // Only animate the upright that was just spent; the rest are already settled.
            bool justSpent = i == wallsLeft && wallsLeft < _lastWalls;
            _walls[i].BeginAnimation(OpacityProperty,
                new DoubleAnimation(target, TimeSpan.FromMilliseconds(justSpent ? 420 : 0)));
        }

        _lastWalls = wallsLeft;

        if (active == _lastActive) return;
        _lastActive = active;

        _turnMark.BeginAnimation(HeightProperty, new DoubleAnimation(active ? 54 : 0, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut },
        });

        Opacity = active ? 1 : 0.55;
    }

    public void RefreshAccent()
    {
        _turnMark.Fill = Palette.BrushOf(_accentKey);
        foreach (Rectangle upright in _walls) upright.Fill = Palette.BrushOf(_accentKey);
    }
}
