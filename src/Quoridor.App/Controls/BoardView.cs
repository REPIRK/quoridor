using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Quoridor.App.Theme;
using Quoridor.Core;

namespace Quoridor.App.Controls;

/// <summary>
/// The board. Everything is drawn in a fixed 728x728 logical space inside a
/// <see cref="Viewbox"/>, so the whole thing is resolution independent: the window
/// can be any size and the geometry below never changes.
///
/// Hover works without a mode switch. The cursor position decides what you are
/// pointing at — a square, or one of the grooves between squares — and the wall
/// preview follows with the orientation that groove implies. Right-click, the
/// wheel or R flips the orientation when you are over an intersection.
///
/// The board can also be turned around, so whoever is sitting in front of it always
/// sees their own pawn at the near edge. That is a half-turn of the whole board, so
/// every conversion between a rules coordinate and a screen coordinate goes through
/// <see cref="ViewIndex"/> / <see cref="ViewSlot"/> — both of which are their own
/// inverse, which is what lets hit-testing run the same mapping backwards.
/// </summary>
public sealed class BoardView : UserControl
{
    // --- geometry -----------------------------------------------------------
    private const double Pad = 28;
    private const double CellSize = 64;
    private const double GapSize = 12;
    private const double Pitch = CellSize + GapSize;
    private const double WallLength = CellSize * 2 + GapSize;
    private const double Extent = Pad * 2 + Board.Size * CellSize + (Board.Size - 1) * GapSize;
    private const double PawnRadius = 20;
    private const double HintRadius = 7;

    /// <summary>How close the cursor must be to a groove centre to mean "wall".</summary>
    private const double GrooveReach = 17;

    // --- layers -------------------------------------------------------------
    private readonly Canvas _root = new() { Width = Extent, Height = Extent, Background = Brushes.Transparent };
    private readonly Canvas _coordinateLayer = new() { IsHitTestVisible = false };
    private readonly Canvas _goalLayer = new() { IsHitTestVisible = false };
    private readonly Canvas _hintLayer = new();
    private readonly Canvas _routeLayer = new();
    private readonly Canvas _wallLayer = new();
    private readonly Canvas _pawnLayer = new();
    private readonly Rectangle _highlight = new();
    private readonly Rectangle _lastMoveMark = new();
    private readonly Rectangle _ghost = new();
    private readonly Ellipse[] _pawnShapes = new Ellipse[2];
    private readonly DropShadowEffect[] _pawnGlow = new DropShadowEffect[2];

    private GameState _state;
    private bool _interactive;
    private bool _busy;
    private bool _flipped;
    private bool _preferHorizontal = true;
    private bool _showRoutes;
    private int _hoverCell = -1;

    /// <summary>The square rectangles, in screen order, so holes can be restyled later.</summary>
    private readonly Rectangle[] _cells = new Rectangle[Board.CellCount];

    private readonly Canvas _frame = new();
    private readonly Rectangle _backdrop = new();
    private readonly Canvas _pickupLayer = new();
    private readonly Canvas _portalLayer = new();

    private UInt128 _holes;

    /// <summary>First row of the game as last drawn, so the frame is only redone on a change.</summary>
    private int _framedFrom = -1;
    private Move? _ghostMove;
    private bool _ghostLegal;

    public BoardView()
    {
        _state = GameState.CreateInitial();

        BuildVisuals();

        // A smaller game is played on a centred square of the same grid, so the drawing
        // never changes — the frame simply shows less of it, and the root slides under.
        _frame.Width = Extent;
        _frame.Height = Extent;
        _frame.ClipToBounds = true;
        _frame.Children.Add(_root);

        Content = new Viewbox
        {
            Stretch = Stretch.Uniform,
            Child = _frame,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _root.MouseMove += OnMouseMove;
        _root.MouseLeave += (_, _) => ClearHover();
        _root.MouseLeftButtonDown += OnLeftClick;
        _root.MouseRightButtonDown += (_, _) => ToggleWallOrientation();
        _root.MouseWheel += (_, _) => ToggleWallOrientation();

        // Palette.Changed is static, so an un-subscribed board would be kept alive by
        // it for the lifetime of the app — one leak per game started.
        Action<AppTheme> onThemeChanged = _ => RefreshThemeColours();
        Palette.Changed += onThemeChanged;
        Unloaded += (_, _) => Palette.Changed -= onThemeChanged;
    }

    /// <summary>
    /// What the wall under the cursor would actually do, in steps added to each side's
    /// route. Checking a wall's legality already walks both routes, so the number that
    /// decides whether it is worth playing costs almost nothing to report.
    /// </summary>
    public readonly record struct WallPreview(bool Legal, bool WouldSeal, int CostToOpponent, int CostToMover);

    /// <summary>Raised when the player commits to a move. The host validates and applies it.</summary>
    public event EventHandler<Move>? MoveChosen;

    /// <summary>Raised as the wall preview moves, and with null when it goes away.</summary>
    public event EventHandler<WallPreview?>? WallPreviewChanged;

    /// <summary>Whether the local player may interact right now.</summary>
    public bool IsInteractive
    {
        get => _interactive;
        set
        {
            if (_interactive == value) return;
            _interactive = value;
            if (!value) ClearHover();
            RefreshOverlays();
        }
    }

    /// <summary>
    /// Turns the board around, so player 1 sits at the top and player 2 at the bottom.
    /// Used when the local player takes the second seat: your own pawn belongs at the
    /// near edge no matter which colour you were given.
    /// </summary>
    public bool Flipped
    {
        get => _flipped;
        set
        {
            if (_flipped == value) return;

            _flipped = value;
            BuildCoordinates();
            BuildGoalMarks();
            RefreshCells();
            Reset(_state);
        }
    }

    /// <summary>
    /// The squares taken out of play, as a cell mask. Drawn as gaps in the board rather
    /// than as darker squares, so they read as somewhere there is nothing to stand on.
    /// </summary>
    public UInt128 Holes
    {
        get => _holes;
        set
        {
            if (_holes == value) return;

            _holes = value;
            RefreshCells();
        }
    }

    /// <summary>
    /// Styles every square: a plain one, a hole, or one of the ring outside a smaller
    /// game — which is hidden outright, because the frame leaves a clean margin around
    /// the playable square and the ring's squares begin half a cell inside it.
    /// </summary>
    private void RefreshCells()
    {
        int from = Math.Max(0, _framedFrom);
        int last = Board.Size - 1 - from;

        for (int row = 0; row < Board.Size; row++)
        {
            for (int col = 0; col < Board.Size; col++)
            {
                // The squares were laid out in screen order, so a model cell has to be
                // mapped through the same turn the rest of the board uses.
                Rectangle square = _cells[Board.Index(ViewIndex(row), ViewIndex(col))];

                if (row < from || row > last || col < from || col > last)
                {
                    square.Visibility = Visibility.Collapsed;
                    continue;
                }

                bool hole = (_holes & Board.Bit(Board.Index(row, col))) != 0;

                square.Visibility = Visibility.Visible;
                square.Fill = hole ? null : Palette.BrushOf(Palette.Cell);
                square.Stroke = hole ? Palette.BrushOf(Palette.Line) : null;
                square.StrokeThickness = hole ? 1 : 0;
                square.StrokeDashArray = hole ? new DoubleCollection { 3, 5 } : null;
                square.Opacity = hole ? 0.5 : 0.9;
            }
        }
    }

    /// <summary>
    /// Draws both players' current shortest routes. Quoridor is a game about the length
    /// of a route, and the route is the one thing a board does not show you.
    /// </summary>
    public bool ShowRoutes
    {
        get => _showRoutes;
        set
        {
            if (_showRoutes == value) return;
            _showRoutes = value;
            RefreshRoutes();
        }
    }

    // ============================================================== building ==

    private void BuildVisuals()
    {
        _backdrop.Width = Extent;
        _backdrop.Height = Extent;
        _backdrop.RadiusX = 6;
        _backdrop.RadiusY = 6;
        // A shallow top-to-bottom gradient rather than one flat tone, so the board sits
        // on the page as an object. Built from the board colour so it follows the theme.
        _backdrop.Fill = BoardSheen();
        _backdrop.Stroke = Palette.BrushOf(Palette.Line);
        _backdrop.StrokeThickness = 1;
        Place(_backdrop, 0, 0);
        _root.Children.Add(_backdrop);

        _root.Children.Add(_coordinateLayer);
        BuildCoordinates();

        // The squares themselves are identical, so they are laid out in screen order and
        // never need to know which way round the board is.
        var cellLayer = new Canvas();
        for (int row = 0; row < Board.Size; row++)
        {
            for (int col = 0; col < Board.Size; col++)
            {
                var cell = new Rectangle
                {
                    Width = CellSize,
                    Height = CellSize,
                    RadiusX = 3,
                    RadiusY = 3,
                    Fill = Palette.BrushOf(Palette.Cell),
                    Opacity = 0.9,
                };
                Place(cell, Origin(col), Origin(row));
                cellLayer.Children.Add(cell);
                _cells[Board.Index(row, col)] = cell;
            }
        }

        _root.Children.Add(cellLayer);

        _root.Children.Add(_goalLayer);
        BuildGoalMarks();

        _highlight.Width = CellSize;
        _highlight.Height = CellSize;
        _highlight.RadiusX = 3;
        _highlight.RadiusY = 3;
        _highlight.Fill = Palette.BrushOf(Palette.Accent0);
        _highlight.Opacity = 0;
        _highlight.IsHitTestVisible = false;
        Place(_highlight, Pad, Pad);
        _root.Children.Add(_highlight);

        _lastMoveMark.RadiusX = 3;
        _lastMoveMark.RadiusY = 3;
        _lastMoveMark.Fill = null;
        _lastMoveMark.StrokeThickness = 1.5;
        _lastMoveMark.Opacity = 0;
        _lastMoveMark.IsHitTestVisible = false;
        _root.Children.Add(_lastMoveMark);

        _routeLayer.IsHitTestVisible = false;
        _root.Children.Add(_routeLayer);

        _hintLayer.IsHitTestVisible = false;
        _root.Children.Add(_hintLayer);

        _portalLayer.IsHitTestVisible = false;
        _root.Children.Add(_portalLayer);

        _pickupLayer.IsHitTestVisible = false;
        _root.Children.Add(_pickupLayer);

        _root.Children.Add(_wallLayer);

        _ghost.RadiusX = 2;
        _ghost.RadiusY = 2;
        _ghost.Fill = Palette.BrushOf(Palette.Wall);
        _ghost.StrokeThickness = 1.2;
        _ghost.Opacity = 0;
        _ghost.IsHitTestVisible = false;
        _root.Children.Add(_ghost);

        _pawnLayer.IsHitTestVisible = false;
        for (int player = 0; player < 2; player++)
        {
            // Kept small: a wide neon halo is the least paper-like thing a board can do.
            var glow = new DropShadowEffect
            {
                Color = Palette.ColorOf(player == 0 ? Palette.Accent0 : Palette.Accent1),
                BlurRadius = 18,
                ShadowDepth = 0,
                Opacity = 0.2,
            };

            var pawn = new Ellipse
            {
                Width = PawnRadius * 2,
                Height = PawnRadius * 2,
                Fill = Palette.BrushOf(player == 0 ? Palette.Accent0 : Palette.Accent1),
                Effect = glow,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1),
            };

            _pawnGlow[player] = glow;
            _pawnShapes[player] = pawn;
            _pawnLayer.Children.Add(pawn);
        }

        _root.Children.Add(_pawnLayer);
    }

    /// <summary>
    /// Each player's target row: a faint wash square by square so the grid keeps its
    /// rhythm, plus a finish line ruled along the outer edge. The line is what the eye
    /// actually reads; the wash only says which end of the board is whose.
    /// </summary>
    private void BuildGoalMarks()
    {
        _goalLayer.Children.Clear();

        int from = _state.GoalRow(0);
        int last = _state.GoalRow(1);
        int span = last - from + 1;

        double contentWidth = span * CellSize + (span - 1) * GapSize;

        for (int player = 0; player < 2; player++)
        {
            int viewRow = ViewIndex(_state.GoalRow(player));
            string accent = player == 0 ? Palette.Accent0 : Palette.Accent1;

            for (int col = from; col <= last; col++)
            {
                var wash = new Rectangle
                {
                    Width = CellSize,
                    Height = CellSize,
                    RadiusX = 3,
                    RadiusY = 3,
                    Fill = Palette.BrushOf(accent),
                    Opacity = 0.09,
                };

                Place(wash, Origin(col), Origin(viewRow));
                _goalLayer.Children.Add(wash);
            }

            var finish = new Rectangle
            {
                Width = contentWidth,
                Height = 2.5,
                Fill = Palette.BrushOf(accent),
                Opacity = 0.7,
            };

            // Along the outer edge of whichever screen row the goal ended up on.
            Place(finish, Origin(from), viewRow == from ? Origin(from) : Origin(viewRow) + CellSize - 2.5);
            _goalLayer.Children.Add(finish);
        }
    }

    /// <summary>
    /// The board's surface: its own colour, lifted a little at the top and dropped a
    /// little at the bottom. Small enough to read as light rather than as a pattern.
    /// </summary>
    private static LinearGradientBrush BoardSheen()
    {
        Color the = ((SolidColorBrush)Palette.BrushOf(Palette.BoardSurface)).Color;

        static Color Shift(Color colour, int by)
        {
            static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);
            return Color.FromRgb(Clamp(colour.R + by), Clamp(colour.G + by), Clamp(colour.B + by));
        }

        return new LinearGradientBrush(Shift(the, 5), Shift(the, -4), 90);
    }

    private void RefreshThemeColours()
    {
        for (int player = 0; player < 2; player++)
            _pawnGlow[player].Color = Palette.ColorOf(player == 0 ? Palette.Accent0 : Palette.Accent1);

        // The gradient is mixed from the board colour, so it has to be mixed again when
        // that colour changes. The rest of the board rides DynamicResource and does not.
        _backdrop.Fill = BoardSheen();

        // The portal inks are likewise raw colours rather than shared brushes.
        RefreshPortals();
    }

    /// <summary>
    /// Files a-i and ranks 1-9 printed in the board's own margin, the way they are on a
    /// physical board. They follow the board around when it is turned, so the labels
    /// always name the square you are actually looking at.
    /// </summary>
    private void BuildCoordinates()
    {
        _coordinateLayer.Children.Clear();

        // Only the squares in the game are labelled, and the labels sit against the
        // edges of what is shown rather than the edges of the whole grid.
        int from = Math.Max(0, _framedFrom);
        int last = Board.Size - 1 - from;

        double left = from * Pitch + 4;
        double bottom = (Board.Size - 1 - from) * Pitch + CellSize + Pad + 5;

        // Counted from the game's own corner: a 7x7 board runs a1 to g7, which is also
        // what the move list calls those squares.
        for (int i = from; i <= last; i++)
        {
            int logical = ViewIndex(i);

            _coordinateLayer.Children.Add(Label($"{last - logical + 1}", left, Centre(i) - 9));
            _coordinateLayer.Children.Add(Label($"{(char)('a' + logical - from)}", Centre(i) - 10, bottom));
        }

        static TextBlock Label(string text, double x, double y)
        {
            var label = new TextBlock
            {
                Text = text,
                Width = 20,
                TextAlignment = TextAlignment.Center,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = Palette.BrushOf(Palette.Muted),
                Opacity = 0.75,
            };

            Place(label, x, y);
            return label;
        }
    }

    // ============================================================== position ==

    /// <summary>
    /// Shows only the square the game is actually played on. Nothing that draws the
    /// board changes: the frame is narrowed and the full-size root slides underneath,
    /// which also means the ring outside stops receiving the mouse.
    /// </summary>
    private void FrameBoard()
    {
        int from = _state.GoalRow(0);
        if (from == _framedFrom) return;

        _framedFrom = from;

        int span = _state.GoalRow(1) - from + 1;
        double size = (span - 1) * Pitch + CellSize + Pad * 2;
        double offset = from * Pitch;

        _frame.Width = size;
        _frame.Height = size;

        Canvas.SetLeft(_root, -offset);
        Canvas.SetTop(_root, -offset);

        _backdrop.Width = size;
        _backdrop.Height = size;
        Place(_backdrop, offset, offset);

        BuildCoordinates();
        BuildGoalMarks();
        RefreshCells();
    }

    /// <summary>Whatever is still lying on the board waiting to be stepped on.</summary>
    private void RefreshPickups()
    {
        _pickupLayer.Children.Clear();

        UInt128 walls = _state.WallPickups;
        UInt128 skips = _state.SkipPickups;

        for (int cell = 0; cell < Board.CellCount && (walls | skips) != 0; cell++)
        {
            UInt128 bit = Board.Bit(cell);
            bool isWall = (walls & bit) != 0;

            if (!isWall && (skips & bit) == 0) continue;

            walls &= ~bit;
            skips &= ~bit;

            // The raw model index, as every other caller passes: CellCentreOf turns it
            // round by itself, and ViewIndex is its own inverse, so turning it round here
            // as well cancelled the flip and drew each pickup on the half-turn image of
            // its own square. Invisible at the start of a game, because the pickups are
            // placed in half-turn pairs and the set is therefore flip-invariant — and
            // wrong from the moment one of them is taken.
            double x = CellCentreOf(Board.ColOf(cell));
            double y = CellCentreOf(Board.RowOf(cell));

            Brush ink = Palette.BrushOf(Palette.Text);

            if (isWall)
            {
                // A spare wall, drawn as the thing it gives you.
                var bar = new Rectangle
                {
                    Width = 26,
                    Height = 8,
                    RadiusX = 2,
                    RadiusY = 2,
                    Fill = ink,
                    Opacity = 0.34,
                };
                Place(bar, x - 13, y - 4);
                _pickupLayer.Children.Add(bar);
                continue;
            }

            // A free move: a turn coming round again, drawn as three quarters of a
            // circle with an arrowhead on the end. It says "go again" without a word.
            var arc = new Path
            {
                Data = Geometry.Parse("M 6.36 -6.36 A 9 9 0 1 1 -6.36 -6.36"),
                Stroke = ink,
                StrokeThickness = 2.1,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Opacity = 0.5,
                RenderTransform = new TranslateTransform(x, y),
            };
            _pickupLayer.Children.Add(arc);

            var head = new Path
            {
                Data = Geometry.Parse("M -2.83 -9.90 L -4.10 -4.10 L -8.63 -8.63 Z"),
                Fill = ink,
                Opacity = 0.5,
                RenderTransform = new TranslateTransform(x, y),
            };
            _pickupLayer.Children.Add(head);
        }
    }

    /// <summary>
    /// The portal mouths. Both ends of a pair are drawn in one ink, because the only thing
    /// a player wants from a mouth is where it comes out — and with two pairs on the board
    /// a shared colour says that without a label or a line ruled across the middle.
    ///
    /// Read straight off the position, which is where the pairs live, so nothing has to be
    /// carried here alongside the board the way the holes are.
    /// </summary>
    private void RefreshPortals()
    {
        _portalLayer.Children.Clear();

        ulong pairs = _state.Portals;
        int index = 0;

        while (pairs != 0)
        {
            int low = System.Numerics.BitOperations.TrailingZeroCount(pairs);
            pairs &= pairs - 1;

            Color ink = PortalInk(index++);

            DrawMouth(low, ink);
            DrawMouth(GameState.PortalPartner(low), ink);
        }

        void DrawMouth(int cell, Color ink)
        {
            double x = CellCentreOf(Board.ColOf(cell));
            double y = CellCentreOf(Board.RowOf(cell));

            // Two rings about one centre, which reads as an opening rather than as a token
            // lying on the square. The outer one is wider than a pawn, so a pawn standing
            // on a mouth sits inside it instead of covering it up.
            var outer = new Ellipse
            {
                Width = 46,
                Height = 46,
                Stroke = new SolidColorBrush(ink),
                StrokeThickness = 2,
                Fill = new SolidColorBrush(ink) { Opacity = 0.12 },
                Opacity = 0.8,
            };

            Place(outer, x - 23, y - 23);
            _portalLayer.Children.Add(outer);

            var inner = new Ellipse
            {
                Width = 24,
                Height = 24,
                Stroke = new SolidColorBrush(ink),
                StrokeThickness = 1.4,
                Opacity = 0.5,
            };

            Place(inner, x - 12, y - 12);
            _portalLayer.Children.Add(inner);
        }
    }

    /// <summary>
    /// The ink for one portal pair. Neither is a player's colour: a mouth belongs to the
    /// board rather than to a side, and a violet mouth beside a teal pawn cannot be read as
    /// that player's property. Mixed here rather than added to the palette because nothing
    /// outside the board ever draws one.
    /// </summary>
    private static Color PortalInk(int pair)
    {
        bool dark = Palette.Current == AppTheme.Dark;

        return (pair & 1) == 0
            ? dark ? Color.FromRgb(0x9B, 0x87, 0xCF) : Color.FromRgb(0x5A, 0x46, 0x8F)
            : dark ? Color.FromRgb(0xC9, 0xA7, 0x52) : Color.FromRgb(0x83, 0x63, 0x14);
    }

    /// <summary>Snaps the board to a position with no animation (new game, undo, restart).</summary>
    public void Reset(GameState state, Move? lastMove = null)
    {
        _state = state;
        _busy = false;

        FrameBoard();
        _wallLayer.Children.Clear();

        for (int slot = 0; slot < Board.SlotCount; slot++)
        {
            int row = slot / Board.SlotSize;
            int col = slot % Board.SlotSize;
            int owner = state.WallOwner(slot);

            if ((state.HorizontalWalls & (1UL << slot)) != 0)
                _wallLayer.Children.Add(CreateWall(MoveKind.HorizontalWall, row, col, owner));

            if ((state.VerticalWalls & (1UL << slot)) != 0)
                _wallLayer.Children.Add(CreateWall(MoveKind.VerticalWall, row, col, owner));
        }

        for (int player = 0; player < 2; player++) SnapPawn(player);

        if (lastMove is { } move) MarkLastMove(move, state.SideToMove ^ 1);
        else HideLastMove();

        ClearHover();
        RefreshOverlays();
    }

    /// <summary>
    /// Applies a move with its animation. Completes once the motion has settled, so
    /// the caller can chain the bot's reply without the two overlapping.
    /// </summary>
    public Task PlayAsync(Move move, GameState after)
    {
        _busy = true;
        ClearHover();
        ClearHints();

        var done = new TaskCompletionSource();

        int mover = after.SideToMove ^ 1;

        if (move.Kind == MoveKind.Pawn)
        {
            AnimatePawn(mover, move.Cell, () => Finish());
        }
        else
        {
            Rectangle wall = CreateWall(move.Kind, move.Row, move.Col, mover);
            _wallLayer.Children.Add(wall);
            AnimateWallEntry(wall, move.IsHorizontal, () => Finish());
        }

        return done.Task;

        void Finish()
        {
            _state = after;
            _busy = false;
            MarkLastMove(move, mover);
            RefreshOverlays();
            done.TrySetResult();
        }
    }

    private void SnapPawn(int player)
    {
        Ellipse pawn = _pawnShapes[player];
        int cell = _state.PawnOf(player);

        pawn.BeginAnimation(Canvas.LeftProperty, null);
        pawn.BeginAnimation(Canvas.TopProperty, null);

        Place(pawn, CellCentreOf(Board.ColOf(cell)) - PawnRadius, CellCentreOf(Board.RowOf(cell)) - PawnRadius);
    }

    private void AnimatePawn(int player, int targetCell, Action completed)
    {
        Ellipse pawn = _pawnShapes[player];

        // The board still shows the position before the move, so the square being left is
        // the one the position records.
        int from = _state.PawnOf(player);

        if (_state.IsPortalMouth(from) && GameState.PortalPartner(from) == targetCell)
        {
            WarpPawn(player, targetCell, completed);
            return;
        }

        double x = CellCentreOf(Board.ColOf(targetCell)) - PawnRadius;
        double y = CellCentreOf(Board.RowOf(targetCell)) - PawnRadius;

        var duration = TimeSpan.FromMilliseconds(300);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        var slideX = new DoubleAnimation(x, duration) { EasingFunction = ease };
        var slideY = new DoubleAnimation(y, duration) { EasingFunction = ease };
        slideY.Completed += (_, _) => completed();

        pawn.BeginAnimation(Canvas.LeftProperty, slideX);
        pawn.BeginAnimation(Canvas.TopProperty, slideY);

        // A small stretch on departure and settle on arrival: the pawn reads as a
        // physical piece being lifted rather than a sprite teleporting.
        var pulse = new DoubleAnimationUsingKeyFrames { Duration = duration };
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(0)));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.16, KeyTime.FromPercent(0.35),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1),
            new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 }));

        var scale = (ScaleTransform)pawn.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
    }

    /// <summary>
    /// The one pawn move that is not a slide. The two mouths of a portal are half a board
    /// apart, so a pawn gliding between them would draw a diagonal over ground it never
    /// walked and over walls it never passed. Instead it is drawn out at the near mouth and
    /// back in at the far one, and nothing crosses the space between: leave, then arrive.
    /// </summary>
    private void WarpPawn(int player, int targetCell, Action completed)
    {
        Ellipse pawn = _pawnShapes[player];
        var scale = (ScaleTransform)pawn.RenderTransform;

        var duration = TimeSpan.FromMilliseconds(170);

        var shrink = new DoubleAnimation(1, 0.28, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };

        // Hung on the fade rather than on the scale, because one animation object drives
        // both axes and its Completed would otherwise run twice.
        var fadeOut = new DoubleAnimation(0, duration);
        fadeOut.Completed += (_, _) => Arrive();

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        pawn.BeginAnimation(OpacityProperty, fadeOut);

        void Arrive()
        {
            // Moved while it cannot be seen, so there is no motion between the mouths.
            pawn.BeginAnimation(Canvas.LeftProperty, null);
            pawn.BeginAnimation(Canvas.TopProperty, null);
            Place(
                pawn,
                CellCentreOf(Board.ColOf(targetCell)) - PawnRadius,
                CellCentreOf(Board.RowOf(targetCell)) - PawnRadius);

            var back = TimeSpan.FromMilliseconds(240);

            var grow = new DoubleAnimation(0.28, 1, back)
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.8 },
            };

            var fadeIn = new DoubleAnimation(0, 1, back);
            fadeIn.Completed += (_, _) => completed();

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            pawn.BeginAnimation(OpacityProperty, fadeIn);
        }
    }

    private Rectangle CreateWall(MoveKind kind, int row, int col, int owner)
    {
        bool horizontal = kind == MoveKind.HorizontalWall;

        // Walls carry their owner's colour. The physical game uses neutral pieces, but
        // on screen it is the difference between reading the position at a glance and
        // having to reconstruct who built what.
        var wall = new Rectangle
        {
            Width = horizontal ? WallLength : GapSize,
            Height = horizontal ? GapSize : WallLength,
            RadiusX = 2,
            RadiusY = 2,
            Fill = Palette.BrushOf(owner == 0 ? Palette.Accent0 : Palette.Accent1),
            Stroke = Palette.BrushOf(Palette.Wall),
            StrokeThickness = 0.75,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 10,
                ShadowDepth = 1.5,
                Direction = 270,
                Opacity = 0.22,
            },
        };

        Place(wall, WallXOf(kind, col), WallYOf(kind, row));
        return wall;
    }

    private static void AnimateWallEntry(Rectangle wall, bool horizontal, Action completed)
    {
        var scale = new ScaleTransform(horizontal ? 0.06 : 1, horizontal ? 1 : 0.06);
        wall.RenderTransform = scale;
        wall.Opacity = 0;

        var duration = TimeSpan.FromMilliseconds(360);
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };

        var grow = new DoubleAnimation(1, duration) { EasingFunction = ease };
        grow.Completed += (_, _) => completed();

        // Walls snap in along their length — the direction they were slotted from.
        scale.BeginAnimation(horizontal ? ScaleTransform.ScaleXProperty : ScaleTransform.ScaleYProperty, grow);
        wall.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(170)));
    }

    // ================================================================= hover ==

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_interactive || _busy) return;
        UpdateHover(e.GetPosition(_root));
    }

    public void ToggleWallOrientation()
    {
        _preferHorizontal = !_preferHorizontal;
        if (_interactive && !_busy && _root.IsMouseOver)
            UpdateHover(Mouse.GetPosition(_root));
    }

    private void UpdateHover(Point point)
    {
        // Everything here is worked out in screen terms first and converted to rules
        // coordinates at the end — the mapping is an involution, so the same helpers
        // run in both directions.
        int grooveCol = ClampSlot((int)Math.Round((point.X - Pad - CellSize - GapSize / 2) / Pitch));
        int grooveRow = ClampSlot((int)Math.Round((point.Y - Pad - CellSize - GapSize / 2) / Pitch));

        double distanceToVertical = Math.Abs(point.X - GrooveCentre(grooveCol));
        double distanceToHorizontal = Math.Abs(point.Y - GrooveCentre(grooveRow));

        bool nearVertical = distanceToVertical <= GrooveReach;
        bool nearHorizontal = distanceToHorizontal <= GrooveReach;

        if (nearVertical || nearHorizontal)
        {
            bool horizontal = nearVertical && nearHorizontal
                ? _preferHorizontal
                : nearHorizontal;

            int viewRow = horizontal ? grooveRow : ClampSlot((int)Math.Round((point.Y - Pad - WallLength / 2) / Pitch));
            int viewCol = horizontal ? ClampSlot((int)Math.Round((point.X - Pad - WallLength / 2) / Pitch)) : grooveCol;

            ShowGhost(
                horizontal ? MoveKind.HorizontalWall : MoveKind.VerticalWall,
                ViewSlot(viewRow),
                ViewSlot(viewCol));

            HideHighlight();
            return;
        }

        HideGhost();

        // Clamped to the game rather than the grid, so the margin around a smaller board
        // cannot land the cursor on a square that is not in play.
        int first = Math.Max(0, _framedFrom);
        int final = Board.Size - 1 - first;

        int cellCol = ViewIndex(Math.Clamp((int)((point.X - Pad) / Pitch), first, final));
        int cellRow = ViewIndex(Math.Clamp((int)((point.Y - Pad) / Pitch), first, final));

        if (_state.IsPawnMoveLegal(cellRow, cellCol))
        {
            MoveHighlightTo(Board.Index(cellRow, cellCol));
            Cursor = Cursors.Hand;
        }
        else
        {
            HideHighlight();
            Cursor = Cursors.Arrow;
        }
    }

    private void ShowGhost(MoveKind kind, int row, int col)
    {
        bool horizontal = kind == MoveKind.HorizontalWall;
        bool legal = _state.IsWallLegal(kind, row, col);

        var candidate = new Move(kind, row, col);
        bool sameTarget = _ghostMove == candidate && _ghostLegal == legal;

        _ghostMove = candidate;
        _ghostLegal = legal;
        Cursor = legal ? Cursors.Hand : Cursors.Arrow;

        _ghost.Width = horizontal ? WallLength : GapSize;
        _ghost.Height = horizontal ? GapSize : WallLength;

        // A legal wall previews as the wall itself, half inked. An illegal one is drawn
        // as an empty outline — it reads as "not a wall" without needing a warning colour
        // that would clash with a player.
        if (legal)
        {
            _ghost.Fill = Palette.BrushOf(_state.SideToMove == 0 ? Palette.Accent0 : Palette.Accent1);
            _ghost.Stroke = null;
        }
        else
        {
            _ghost.Fill = null;
            _ghost.Stroke = Palette.BrushOf(Palette.Danger);
        }

        _ghost.BeginAnimation(Canvas.LeftProperty, null);
        _ghost.BeginAnimation(Canvas.TopProperty, null);
        Place(_ghost, WallXOf(kind, col), WallYOf(kind, row));

        if (sameTarget) return;

        _ghost.BeginAnimation(OpacityProperty,
            new DoubleAnimation(legal ? 0.5 : 0.3, TimeSpan.FromMilliseconds(110)));

        WallPreviewChanged?.Invoke(this, MeasureWall(kind, row, col, legal));
    }

    /// <summary>Steps this wall would add to each side's shortest route.</summary>
    private WallPreview MeasureWall(MoveKind kind, int row, int col, bool legal)
    {
        bool fits = _state.IsSlotFree(kind, row, col);

        if (!legal) return new WallPreview(false, WouldSeal: fits, 0, 0);

        int mover = _state.SideToMove;
        int opponent = mover ^ 1;

        GameState probe = _state;
        probe.PlaceWallUnchecked(kind, row, col);

        return new WallPreview(
            true,
            WouldSeal: false,
            PathFinder.Distance(probe, opponent) - PathFinder.Distance(_state, opponent),
            PathFinder.Distance(probe, mover) - PathFinder.Distance(_state, mover));
    }

    private void HideGhost()
    {
        if (_ghostMove is null) return;

        _ghostMove = null;
        _ghost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(110)));
        WallPreviewChanged?.Invoke(this, null);
    }

    private void MoveHighlightTo(int cell)
    {
        if (_hoverCell == cell) return;

        double x = CellOriginOf(Board.ColOf(cell));
        double y = CellOriginOf(Board.RowOf(cell));

        _highlight.Fill = Palette.BrushOf(_state.SideToMove == 0 ? Palette.Accent0 : Palette.Accent1);

        if (_hoverCell < 0)
        {
            _highlight.BeginAnimation(Canvas.LeftProperty, null);
            _highlight.BeginAnimation(Canvas.TopProperty, null);
            Place(_highlight, x, y);
            _highlight.BeginAnimation(OpacityProperty, new DoubleAnimation(0.16, TimeSpan.FromMilliseconds(130)));
        }
        else
        {
            // Gliding between squares rather than blinking keeps the eye anchored.
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(170);
            _highlight.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(x, duration) { EasingFunction = ease });
            _highlight.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(y, duration) { EasingFunction = ease });
        }

        _hoverCell = cell;
    }

    private void HideHighlight()
    {
        if (_hoverCell < 0) return;

        _hoverCell = -1;
        _highlight.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(130)));
    }

    private void ClearHover()
    {
        HideGhost();
        HideHighlight();
        Cursor = Cursors.Arrow;
    }

    /// <summary>
    /// Re-reads what the cursor is pointing at without waiting for it to move.
    ///
    /// Hover normally only updates on mouse movement, so a cursor left resting on the
    /// square you intend to take is dead when your turn arrives — you would have to
    /// move off it and back before the click registered. The same applies after any
    /// move, when what is legal under the cursor has changed.
    /// </summary>
    private void RefreshHover()
    {
        if (!_interactive || _busy || !_root.IsMouseOver) return;

        UpdateHover(Mouse.GetPosition(_root));
    }

    private void OnLeftClick(object sender, MouseButtonEventArgs e)
    {
        if (!_interactive || _busy) return;

        if (_ghostMove is { } wall)
        {
            if (_ghostLegal) MoveChosen?.Invoke(this, wall);
            return;
        }

        if (_hoverCell >= 0)
            MoveChosen?.Invoke(this, Move.ToCell(_hoverCell));
    }

    // ============================================================== overlays ==

    private void RefreshOverlays()
    {
        for (int player = 0; player < 2; player++) SnapPawn(player);

        ClearHints();

        int active = _state.SideToMove;
        bool live = !_state.IsGameOver;

        for (int player = 0; player < 2; player++)
            SetPulse(player, live && player == active);

        RefreshRoutes();
        RefreshPortals();
        RefreshPickups();
        RefreshHover();

        if (!_interactive || !live) return;

        Span<Move> buffer = stackalloc Move[10];
        int count = _state.GeneratePawnMoves(buffer);

        SolidColorBrush brush = Palette.BrushOf(active == 0 ? Palette.Accent0 : Palette.Accent1);

        for (int i = 0; i < count; i++)
        {
            int cell = buffer[i].Cell;

            var dot = new Ellipse
            {
                Width = HintRadius * 2,
                Height = HintRadius * 2,
                Fill = brush,
                Opacity = 0,
            };

            Place(dot, CellCentreOf(Board.ColOf(cell)) - HintRadius, CellCentreOf(Board.RowOf(cell)) - HintRadius);
            _hintLayer.Children.Add(dot);

            // Staggered so the options appear to unfold rather than pop in at once.
            dot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.42, TimeSpan.FromMilliseconds(200))
            {
                BeginTime = TimeSpan.FromMilliseconds(40 * i),
            });
        }
    }

    private void ClearHints() => _hintLayer.Children.Clear();

    /// <summary>
    /// Rings whatever just happened. The bot answers while you are looking elsewhere,
    /// and without this you have to diff the board against your memory of it.
    /// </summary>
    private void MarkLastMove(Move move, int mover)
    {
        double x, y, width, height;

        if (move.Kind == MoveKind.Pawn)
        {
            x = CellOriginOf(move.Col) - 2;
            y = CellOriginOf(move.Row) - 2;
            width = height = CellSize + 4;
        }
        else
        {
            bool horizontal = move.IsHorizontal;
            x = WallXOf(move.Kind, move.Col) - 3;
            y = WallYOf(move.Kind, move.Row) - 3;
            width = (horizontal ? WallLength : GapSize) + 6;
            height = (horizontal ? GapSize : WallLength) + 6;
        }

        _lastMoveMark.Width = width;
        _lastMoveMark.Height = height;

        // Outline plus a wash inside it. The outline alone was quiet enough to miss,
        // and the last move is exactly what you look for after glancing away.
        Brush ink = Palette.BrushOf(mover == 0 ? Palette.Accent0 : Palette.Accent1);

        _lastMoveMark.Stroke = ink;
        _lastMoveMark.Fill = new SolidColorBrush(((SolidColorBrush)ink).Color) { Opacity = 0.13 };

        _lastMoveMark.BeginAnimation(Canvas.LeftProperty, null);
        _lastMoveMark.BeginAnimation(Canvas.TopProperty, null);
        Place(_lastMoveMark, x, y);

        _lastMoveMark.BeginAnimation(OpacityProperty, new DoubleAnimation(0.75, TimeSpan.FromMilliseconds(220)));
    }

    private void HideLastMove() =>
        _lastMoveMark.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));

    private void RefreshRoutes()
    {
        _routeLayer.Children.Clear();
        if (!_showRoutes || _state.IsGameOver) return;

        Span<byte> distances = stackalloc byte[Board.CellCount];
        var cells = new List<int>(Board.CellCount);

        for (int player = 0; player < 2; player++)
        {
            cells.Clear();
            PathFinder.FillDistancesToGoal(_state, player, distances);
            PathFinder.TraceShortestPath(_state, player, distances, cells);

            if (cells.Count < 2) continue;

            // A route that goes through a portal is not a route across the board. Its two
            // consecutive cells are half a board apart, and one line joining them would
            // claim the pawn walks the ground — and the walls — in between. The line is cut
            // at every hop and picked up again at the far mouth.
            var line = new PointCollection();
            int previous = -1;

            foreach (int cell in cells)
            {
                if (previous >= 0 && !AreNeighbours(previous, cell))
                {
                    AddRouteLine(line, player);
                    line = new PointCollection();
                }

                line.Add(new Point(CellCentreOf(Board.ColOf(cell)), CellCentreOf(Board.RowOf(cell))));
                previous = cell;
            }

            AddRouteLine(line, player);
        }

        static bool AreNeighbours(int a, int b) =>
            Math.Abs(Board.RowOf(a) - Board.RowOf(b)) + Math.Abs(Board.ColOf(a) - Board.ColOf(b)) == 1;
    }

    /// <summary>One unbroken stretch of a player's route. A single point is not a stretch.</summary>
    private void AddRouteLine(PointCollection points, int player)
    {
        if (points.Count < 2) return;

        _routeLayer.Children.Add(new Polyline
        {
            Points = points,
            Stroke = Palette.BrushOf(player == 0 ? Palette.Accent0 : Palette.Accent1),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 1.4, 2.2 },
            StrokeDashCap = PenLineCap.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Opacity = 0.6,
        });
    }

    private void SetPulse(int player, bool active)
    {
        DropShadowEffect glow = _pawnGlow[player];

        if (!active)
        {
            glow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
            glow.Opacity = 0;
            _pawnShapes[player].BeginAnimation(OpacityProperty, null);
            _pawnShapes[player].Opacity = 0.72;
            return;
        }

        _pawnShapes[player].BeginAnimation(OpacityProperty, null);
        _pawnShapes[player].Opacity = 1;

        glow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(0.12, 0.44, TimeSpan.FromMilliseconds(1600))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    // ============================================================== geometry ==

    private static void Place(UIElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
    }

    private static int ClampSlot(int value) => Math.Clamp(value, 0, Board.SlotSize - 1);

    /// <summary>Rules row or column to the screen one, and back. Its own inverse.</summary>
    private int ViewIndex(int index) => _flipped ? Board.Size - 1 - index : index;

    /// <summary>Rules wall slot row or column to the screen one, and back.</summary>
    private int ViewSlot(int index) => _flipped ? Board.SlotSize - 1 - index : index;

    // --- screen space (already-converted indices) ---
    private static double Origin(int viewIndex) => Pad + viewIndex * Pitch;

    private static double Centre(int viewIndex) => Pad + viewIndex * Pitch + CellSize / 2;

    /// <summary>Centre of the groove that follows screen row/column <paramref name="viewIndex"/>.</summary>
    private static double GrooveCentre(int viewIndex) => Pad + viewIndex * Pitch + CellSize + GapSize / 2;

    // --- rules space (converted here) ---
    private double CellOriginOf(int index) => Origin(ViewIndex(index));

    private double CellCentreOf(int index) => Centre(ViewIndex(index));

    private double WallXOf(MoveKind kind, int col) => kind == MoveKind.HorizontalWall
        ? Origin(ViewSlot(col))
        : Origin(ViewSlot(col)) + CellSize;

    private double WallYOf(MoveKind kind, int row) => kind == MoveKind.HorizontalWall
        ? Origin(ViewSlot(row)) + CellSize
        : Origin(ViewSlot(row));
}
