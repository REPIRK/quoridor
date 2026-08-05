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
public sealed class BoardView : UserControl, Views.IBoardAim
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

    /// <summary>
    /// How far apart the two pawns have to be, in steps, before the reading calls a square
    /// decisively one player's. Past this the stain stops deepening: the difference between
    /// six steps clear and nine is not a difference anyone plays on.
    /// </summary>
    private const int Decisive = 6;

    /// <summary>
    /// How far a tile is mixed toward its owner, at a dead heat and at a decisive lead.
    /// The faint end has to be visible as more than noise and the strong end has to stay a
    /// tinted board rather than a coloured one — the reading is a chart drawn on the board,
    /// not a second board laid over it.
    /// </summary>
    private const double FaintestStain = 0.10;

    private const double StrongestStain = 0.44;

    /// <summary>The window a one-shot has to land in, matched to the wall and hint timings.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(320);

    private static readonly TimeSpan Travel = TimeSpan.FromMilliseconds(300);

    // --- layers -------------------------------------------------------------
    private readonly Canvas _root = new() { Width = Extent, Height = Extent, Background = Brushes.Transparent };
    private readonly Canvas _coordinateLayer = new() { IsHitTestVisible = false };
    private readonly Canvas _goalLayer = new() { IsHitTestVisible = false };
    private readonly Canvas _hintLayer = new();
    private readonly Canvas _frontLayer = new();
    private readonly Canvas _routeLayer = new();
    private readonly Canvas _detourLayer = new();
    private readonly Canvas _burstLayer = new();
    private readonly Canvas _wallLayer = new();
    private readonly Canvas _pawnLayer = new();
    private readonly Rectangle _highlight = new();
    private readonly Rectangle _lastMoveMark = new();
    private readonly Rectangle _ghost = new();
    private readonly Rectangle _caret = new();

    /// <summary>
    /// Each pawn is three things that travel together: the shadow it puts on the board, the
    /// ring that says whose turn it is, and the piece itself. The holder carries all three
    /// across the board; the lift inside it carries the piece and its ring <em>over</em>
    /// whatever is being jumped, while the shadow stays down on the squares.
    /// </summary>
    private readonly Canvas[] _pawnHolder = new Canvas[2];

    private readonly Canvas[] _pawnLift = new Canvas[2];
    private readonly Ellipse[] _pawnShapes = new Ellipse[2];
    private readonly Ellipse[] _pawnRing = new Ellipse[2];
    private readonly Ellipse[] _pawnShadow = new Ellipse[2];

    private GameState _state;
    private bool _interactive;
    private bool _busy;
    private bool _flipped;
    private bool _preferHorizontal = true;
    private bool _reading;
    private int _hoverCell = -1;

    /// <summary>Which pawn is wearing the turn ring, so it only lands when it changes hands.</summary>
    private int _ringOn = -1;

    /// <summary>
    /// The reading, per square: whose reach it is in (-1 for neither) and how far mixed
    /// toward them. Held rather than recomputed per draw because one hover redraws the
    /// hints and the routes several times a second and the answer has not moved.
    /// </summary>
    private readonly int[] _owner = new int[Board.CellCount];

    private readonly double[] _stain = new double[Board.CellCount];

    /// <summary>
    /// The stained tile fills, one per player per depth of lead. There are only fourteen
    /// distinct colours a square can take, so they are mixed once and shared rather than
    /// mixed eighty-one times per move — and rebuilt when the theme changes, since they are
    /// derived colours and cannot ride the shared brushes the rest of the board uses.
    /// </summary>
    private readonly SolidColorBrush[,] _stainBrushes = new SolidColorBrush[2, Decisive + 1];

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

    /// <summary>
    /// Where the keyboard's caret is standing, in rules coordinates, and whether pressing
    /// now would be accepted. Held rather than read back off the drawn rectangle because the
    /// board can be turned around or reframed underneath it, and a caret that knew only its
    /// screen position would be left pointing at a different square than the one the player
    /// is steering.
    /// </summary>
    private Move? _caretAt;

    private bool _caretPlayable;

    /// <summary>Who played the move the ring is standing on, so its wash can be mixed again.</summary>
    private int _lastMoveBy = -1;

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

                int cell = Board.Index(row, col);
                bool hole = (_holes & Board.Bit(cell)) != 0;

                square.Visibility = Visibility.Visible;

                // A square out of play is a darker square, edged with the same hairline
                // every other square has. Drawn as a dashed outline it read as a selection
                // marquee somebody had left lying on the board; drawn as the board's own
                // deepest tone it reads as what it is, somewhere there is nothing to stand
                // on — and it keeps its place in the grid instead of becoming a gap in it.
                square.Fill = hole ? Palette.BrushOf(Palette.Pit) : TileFill(cell);
                square.Stroke = Palette.BrushOf(Palette.Line);
                square.StrokeThickness = 0.75;
                square.StrokeDashArray = null;
                square.Opacity = hole ? 1 : 0.9;
            }
        }
    }

    /// <summary>
    /// What one square is painted: its own colour, or that colour mixed toward whichever
    /// player gets there first while the reading is up.
    ///
    /// A tile and the reading of it are the same pixel, so this is a state of the tile and
    /// not a translucent square laid over it. Eighty-one extra shapes to say what eighty-one
    /// existing shapes can say themselves is the difference between a board that is also a
    /// chart and a chart sitting on top of a board.
    /// </summary>
    private Brush TileFill(int cell)
    {
        if (!_reading || _owner[cell] < 0) return Palette.BrushOf(Palette.Cell);

        int player = _owner[cell];
        int depth = (int)Math.Round((_stain[cell] - FaintestStain) / (StrongestStain - FaintestStain) * Decisive);
        depth = Math.Clamp(depth, 0, Decisive);

        return _stainBrushes[player, depth] ??= new SolidColorBrush(Palette.Mix(
            Palette.ColorOf(Palette.Cell),
            Palette.ColorOf(player == 0 ? Palette.Accent0 : Palette.Accent1),
            FaintestStain + (StrongestStain - FaintestStain) * depth / Decisive));
    }

    /// <summary>
    /// Reads the position back to you: both players' routes home, whose reach each square
    /// is in, and the seam where that answer changes.
    ///
    /// One switch and not three. The three are one thought — Quoridor is a game about the
    /// length of a route, and a route, a reach and the line between two reaches are the
    /// same fact seen at three distances. Offering them separately would be three decisions
    /// where the player has one question.
    /// </summary>
    public bool Reading
    {
        get => _reading;
        set
        {
            if (_reading == value) return;
            _reading = value;
            RefreshReading();
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

        // The seam sits under the routes and over the tiles it divides: it is a property of
        // the whole field, and the two routes are answers drawn on top of that field.
        _frontLayer.IsHitTestVisible = false;
        _root.Children.Add(_frontLayer);

        _routeLayer.IsHitTestVisible = false;
        _root.Children.Add(_routeLayer);

        // Above the routes, because the detour is the answer to a question you are asking
        // right now and the routes are the standing state it is asked against.
        _detourLayer.IsHitTestVisible = false;
        _root.Children.Add(_detourLayer);

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

        // The caret the keyboard steers. Above the walls, so one resting in a groove that is
        // already filled is still findable, and below the pieces, so resting it on your own
        // pawn rings the square rather than drawing a box over the piece.
        _caret.Fill = null;
        _caret.StrokeThickness = 1.5;
        _caret.Opacity = 0;
        _caret.IsHitTestVisible = false;
        _root.Children.Add(_caret);

        // A ring opening out of the square a prize was standing on. Above the pieces so a
        // pawn arriving on the prize does not cover the only mark that it was ever there.
        _burstLayer.IsHitTestVisible = false;

        _pawnLayer.IsHitTestVisible = false;
        for (int player = 0; player < 2; player++) BuildPawn(player);

        _root.Children.Add(_pawnLayer);
        _root.Children.Add(_burstLayer);
    }

    /// <summary>
    /// One piece, in three parts about a common origin. Everything inside a holder is drawn
    /// around (0,0), which is why the holder can simply be placed on a square centre and why
    /// the lift's transforms — a rise for a jump, a shrink for a portal — need no origin of
    /// their own: scaling and translating about (0,0) is scaling and translating about the
    /// piece.
    /// </summary>
    private void BuildPawn(int player)
    {
        string accent = player == 0 ? Palette.Accent0 : Palette.Accent1;

        // What the piece puts on the board rather than a glow coming off it. It is what
        // makes a jump read as height: it stays down on the squares while the piece rises,
        // and draws in and darkens as the piece gets further from it.
        var shadow = new Ellipse
        {
            Width = PawnRadius * 2.3,
            Height = PawnRadius * 2.3,
            Fill = ContactInk(),
            Opacity = 0.8,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
        };

        Place(shadow, -PawnRadius * 1.15, -PawnRadius * 1.15 + 2);

        // Switched on and off, never faded. Whose turn it is has to be unambiguous at every
        // instant, and a fade is a window in which it is neither — which is exactly the
        // window a player looks up during. The old board pulsed this forever instead, which
        // is the one thing on a paper board that never stops asking for attention.
        var ring = new Ellipse
        {
            Width = PawnRadius * 2 + 12,
            Height = PawnRadius * 2 + 12,
            Stroke = Palette.BrushOf(accent),
            StrokeThickness = 1.5,
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
        };

        Place(ring, -PawnRadius - 6, -PawnRadius - 6);

        var pawn = new Ellipse
        {
            Width = PawnRadius * 2,
            Height = PawnRadius * 2,
            Fill = Palette.BrushOf(accent),
        };

        Place(pawn, -PawnRadius, -PawnRadius);

        var lift = new Canvas();
        lift.RenderTransform = new TransformGroup
        {
            Children = { new ScaleTransform(1, 1), new TranslateTransform(0, 0) },
        };

        lift.Children.Add(ring);
        lift.Children.Add(pawn);

        var holder = new Canvas();
        holder.Children.Add(shadow);
        holder.Children.Add(lift);

        _pawnShadow[player] = shadow;
        _pawnRing[player] = ring;
        _pawnShapes[player] = pawn;
        _pawnLift[player] = lift;
        _pawnHolder[player] = holder;

        _pawnLayer.Children.Add(holder);
    }

    /// <summary>
    /// The shadow a piece casts: the board's own darkest tone, faded out from the middle so
    /// it has no edge of its own. A hard-edged disc would read as a second piece; a WPF
    /// blur would re-rasterise the whole pawn every frame it moved.
    /// </summary>
    private static RadialGradientBrush ContactInk()
    {
        Color ink = Palette.ColorOf(Palette.Pit);

        return new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x6E, ink.R, ink.G, ink.B), 0),
                new GradientStop(Color.FromArgb(0x40, ink.R, ink.G, ink.B), 0.55),
                new GradientStop(Color.FromArgb(0x00, ink.R, ink.G, ink.B), 1),
            },
        };
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
        // The gradient is mixed from the board colour, so it has to be mixed again when
        // that colour changes. The rest of the board rides DynamicResource and does not.
        _backdrop.Fill = BoardSheen();

        for (int player = 0; player < 2; player++) _pawnShadow[player].Fill = ContactInk();

        // Likewise derived rather than shared: the stains are mixed colours and the portal
        // inks are raw ones, so neither can follow the theme by itself. They land at once
        // while the shared brushes cross-fade around them, which is only visible at all
        // with the reading held up during a theme switch.
        Array.Clear(_stainBrushes);

        ApplyLastMoveInk();
        RefreshPortals();
        RefreshCells();
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

        // The board may have just been turned around or reframed for a smaller game, either
        // of which moves every square out from under whatever was drawn on it. The caret is
        // held in rules coordinates precisely so it can be put back on the square the player
        // is still steering rather than on the pixels it happened to occupy.
        PlaceCaret(animate: false);
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
            // Read off the position the board is still showing, before it is replaced. A
            // prize taken otherwise just stops being drawn, and the only thing that marks
            // it is a sound — which is nothing at all with the sound turned off.
            MarkPickup(move.Cell, mover);
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

    /// <summary>
    /// Sees off a prize that is about to be stepped on: a ring opening outward from the
    /// square and fading, once. Taking one is the only thing on this board that changes the
    /// game without changing the position, so it is the one thing worth watching go.
    ///
    /// A wall goes in the board's neutral ink and a free move in the taker's own, because a
    /// wall is stock and a free move is a turn — and this board already says whose a thing
    /// is by colouring it.
    /// </summary>
    private void MarkPickup(int cell, int taker)
    {
        UInt128 bit = Board.Bit(cell);

        bool wall = (_state.WallPickups & bit) != 0;
        bool skip = (_state.SkipPickups & bit) != 0;

        if (!wall && !skip) return;

        var halo = new Ellipse
        {
            Width = CellSize,
            Height = CellSize,
            Stroke = wall
                ? Palette.BrushOf(Palette.Text)
                : Palette.BrushOf(taker == 0 ? Palette.Accent0 : Palette.Accent1),
            StrokeThickness = 3,
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.35, 0.35),
        };

        Place(halo, CellCentreOf(Board.ColOf(cell)) - CellSize / 2, CellCentreOf(Board.RowOf(cell)) - CellSize / 2);
        _burstLayer.Children.Add(halo);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var open = new DoubleAnimation(0.35, 1.5, Settle) { EasingFunction = ease };

        var fade = new DoubleAnimationUsingKeyFrames { Duration = Settle };
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0.85, KeyTime.FromPercent(0)));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), ease));

        // Taken off the board once it has played, rather than left lying in the layer for
        // the rest of the game: one of these is drawn every time a prize is stepped on.
        fade.Completed += (_, _) => _burstLayer.Children.Remove(halo);

        var scale = (ScaleTransform)halo.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, open);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, open);
        halo.BeginAnimation(OpacityProperty, fade);
    }

    private void SnapPawn(int player)
    {
        Canvas holder = _pawnHolder[player];
        int cell = _state.PawnOf(player);

        holder.BeginAnimation(Canvas.LeftProperty, null);
        holder.BeginAnimation(Canvas.TopProperty, null);
        holder.BeginAnimation(OpacityProperty, null);
        holder.Opacity = 1;

        // A snap can land on top of a move still in flight — a restart during the engine's
        // reply, or a step backwards through the game. Everything the flight was driving is
        // put back, or the piece stays shrunk or hanging in mid-jump on the new position.
        (ScaleTransform scale, TranslateTransform rise) = LiftOf(player);

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = scale.ScaleY = 1;

        rise.BeginAnimation(TranslateTransform.YProperty, null);
        rise.Y = 0;

        SettleShadow(player);

        Place(holder, CellCentreOf(Board.ColOf(cell)), CellCentreOf(Board.RowOf(cell)));
    }

    private (ScaleTransform Scale, TranslateTransform Rise) LiftOf(int player)
    {
        var group = (TransformGroup)_pawnLift[player].RenderTransform;
        return ((ScaleTransform)group.Children[0], (TranslateTransform)group.Children[1]);
    }

    private void SettleShadow(int player)
    {
        Ellipse shadow = _pawnShadow[player];
        var scale = (ScaleTransform)shadow.RenderTransform;

        shadow.BeginAnimation(OpacityProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        shadow.Opacity = 0.8;
        scale.ScaleX = scale.ScaleY = 1;
    }

    private void AnimatePawn(int player, int targetCell, Action completed)
    {
        Canvas holder = _pawnHolder[player];

        // The board still shows the position before the move, so the square being left is
        // the one the position records.
        int from = _state.PawnOf(player);

        if (_state.IsPortalMouth(from) && GameState.PortalPartner(from) == targetCell)
        {
            WarpPawn(player, targetCell, completed);
            return;
        }

        // The piece that is moving passes over the one that is standing still. Set on the
        // layer rather than by reordering it, so nothing is detached mid-flight.
        Panel.SetZIndex(holder, 1);
        Panel.SetZIndex(_pawnHolder[player ^ 1], 0);

        double x = CellCentreOf(Board.ColOf(targetCell));
        double y = CellCentreOf(Board.RowOf(targetCell));

        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        var slideX = new DoubleAnimation(x, Travel) { EasingFunction = ease };
        var slideY = new DoubleAnimation(y, Travel) { EasingFunction = ease };
        slideY.Completed += (_, _) => completed();

        holder.BeginAnimation(Canvas.LeftProperty, slideX);
        holder.BeginAnimation(Canvas.TopProperty, slideY);

        // How far the piece leaves the board, which is the difference between the three
        // things a pawn move can be. A step never leaves it at all; a diagonal slip past a
        // pawn you could not jump straight is a lean; a jump over a pawn is a jump, and
        // has to pass above the piece it is passing.
        int rows = Math.Abs(Board.RowOf(from) - Board.RowOf(targetCell));
        int cols = Math.Abs(Board.ColOf(from) - Board.ColOf(targetCell));
        double rise = rows + cols >= 3 || rows == 2 || cols == 2 ? 17 : rows == 1 && cols == 1 ? 7 : 0;

        // No sign flip on a turned board. The flip here is a remapping of indices rather
        // than a rotation of the drawing, so up the screen is negative Y whichever seat is
        // being played — unlike the browser, where the whole board group is turned and
        // every light-bearing offset has to be negated back.
        Rise(player, rise);
        Contact(player, rise);
    }

    /// <summary>The piece leaving the board and coming back down on the far square.</summary>
    private void Rise(int player, double height)
    {
        (_, TranslateTransform rise) = LiftOf(player);

        if (height <= 0)
        {
            rise.BeginAnimation(TranslateTransform.YProperty, null);
            rise.Y = 0;
            return;
        }

        var arc = new DoubleAnimationUsingKeyFrames { Duration = Travel };
        arc.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        arc.KeyFrames.Add(new EasingDoubleKeyFrame(-height, KeyTime.FromPercent(0.45),
            new SineEase { EasingMode = EasingMode.EaseOut }));
        arc.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1),
            new SineEase { EasingMode = EasingMode.EaseIn }));

        rise.BeginAnimation(TranslateTransform.YProperty, arc);
    }

    /// <summary>
    /// The other half of the height: the shadow stays down on the squares while the piece
    /// is off them, drawing in and darkening as the gap opens and spreading back out as the
    /// piece lands. On the same keyframe as the arc, so the two agree about when the piece
    /// is highest rather than nearly agreeing.
    /// </summary>
    private void Contact(int player, double height)
    {
        Ellipse shadow = _pawnShadow[player];
        var scale = (ScaleTransform)shadow.RenderTransform;

        double tight = height >= 17 ? 0.62 : height > 0 ? 0.82 : 1;
        double dark = height >= 17 ? 1 : height > 0 ? 0.92 : 0.8;

        var spread = new DoubleAnimationUsingKeyFrames { Duration = Travel };
        spread.KeyFrames.Add(new EasingDoubleKeyFrame(1.4, KeyTime.FromPercent(0)));
        spread.KeyFrames.Add(new EasingDoubleKeyFrame(tight, KeyTime.FromPercent(0.45),
            new SineEase { EasingMode = EasingMode.EaseOut }));
        spread.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1),
            new SineEase { EasingMode = EasingMode.EaseIn }));

        var deepen = new DoubleAnimationUsingKeyFrames { Duration = Travel };
        deepen.KeyFrames.Add(new EasingDoubleKeyFrame(0.5, KeyTime.FromPercent(0)));
        deepen.KeyFrames.Add(new EasingDoubleKeyFrame(dark, KeyTime.FromPercent(0.45)));
        deepen.KeyFrames.Add(new EasingDoubleKeyFrame(0.8, KeyTime.FromPercent(1)));

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, spread);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, spread);
        shadow.BeginAnimation(OpacityProperty, deepen);
    }

    /// <summary>
    /// The one pawn move that is not a slide. The two mouths of a portal are half a board
    /// apart, so a pawn gliding between them would draw a diagonal over ground it never
    /// walked and over walls it never passed. Instead it is drawn out at the near mouth and
    /// back in at the far one, and nothing crosses the space between: leave, then arrive.
    /// </summary>
    private void WarpPawn(int player, int targetCell, Action completed)
    {
        Canvas holder = _pawnHolder[player];
        (ScaleTransform scale, _) = LiftOf(player);

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

        // On the holder, so the shadow goes down the mouth with the piece. A shadow left
        // standing on an empty square is the one thing that would say the piece is still
        // on it, and this is the move where it demonstrably is not.
        holder.BeginAnimation(OpacityProperty, fadeOut);

        void Arrive()
        {
            // Moved while it cannot be seen, so there is no motion between the mouths.
            holder.BeginAnimation(Canvas.LeftProperty, null);
            holder.BeginAnimation(Canvas.TopProperty, null);
            Place(holder, CellCentreOf(Board.ColOf(targetCell)), CellCentreOf(Board.RowOf(targetCell)));

            var back = TimeSpan.FromMilliseconds(240);

            var grow = new DoubleAnimation(0.28, 1, back)
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.8 },
            };

            var fadeIn = new DoubleAnimation(0, 1, back);
            fadeIn.Completed += (_, _) => completed();

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            holder.BeginAnimation(OpacityProperty, fadeIn);
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
        ShowDetour(kind, row, col, legal);
    }

    // ================================================================ detour ==

    /// <summary>
    /// What the wall under the cursor would actually do to the route it is aimed at: the
    /// stretch of the old way that dies, and the way round.
    ///
    /// The number in the panel says how much longer. This says where, which is the part you
    /// choose a wall on — whether the detour goes the way you wanted it to go. Both arcs are
    /// cut to the part that differs and extended one square past each end, so they leave and
    /// rejoin the same route rather than being two drawings of the whole board.
    /// </summary>
    private void ShowDetour(MoveKind kind, int row, int col, bool legal)
    {
        _detourLayer.Children.Clear();

        if (!legal || _state.IsGameOver) return;

        // The wall is played against the other player, so theirs is the route it bends.
        int player = _state.SideToMove ^ 1;

        Span<byte> distances = stackalloc byte[Board.CellCount];

        var before = new List<int>(Board.CellCount);
        PathFinder.FillDistancesToGoal(_state, player, distances);
        PathFinder.TraceShortestPath(_state, player, distances, before);
        if (before.Count < 2) return;

        GameState placed = _state;
        placed.PlaceWallUnchecked(kind, row, col);

        UInt128 old = 0;
        foreach (int cell in before) old |= Board.Bit(cell);

        var after = new List<int>(Board.CellCount);
        PathFinder.FillDistancesToGoal(placed, player, distances);
        TraceFamiliar(placed, player, distances, old, after);
        if (after.Count < 2) return;

        UInt128 taken = 0;
        foreach (int cell in after) taken |= Board.Bit(cell);

        // Cut at the first portal each stretch takes rather than broken into pieces at every
        // one: unlike a route ribbon, the new way round is a single line that draws itself
        // in, and both the drawing and the length it is drawn over assume one unbroken run.
        // What is kept is the part nearest the pawn, which is the part being asked about.
        List<int> gone = Unbroken(Diverging(before, taken));
        List<int> now = Unbroken(Diverging(after, old));

        // The wall bends nothing this player was going to do anyway. The panel still says
        // what it costs; there is simply no detour to draw.
        if (gone.Count < 2 && now.Count < 2) return;

        string accent = player == 0 ? Palette.Accent0 : Palette.Accent1;

        if (gone.Count >= 2)
        {
            Polyline dying = Ribbon(Centres(gone), accent, 2, 0.34);
            dying.StrokeDashArray = new DoubleCollection { 1.5, 2.5 };
            dying.StrokeDashCap = PenLineCap.Round;
            _detourLayer.Children.Add(dying);
        }

        if (now.Count < 2) return;

        Polyline way = Ribbon(Centres(now), accent, 3, 0.9);

        // The new way draws itself in rather than appearing, because the point of it is that
        // it is longer, and a line you watch being drawn is a line you have felt the length
        // of. Every segment of a route is exactly one pitch, so the length is arithmetic and
        // needs no measuring pass — but WPF states dashes in multiples of the stroke width
        // rather than in units, so both numbers are divided by it on the way in.
        double length = (now.Count - 1) * Pitch / way.StrokeThickness;

        way.StrokeDashArray = new DoubleCollection { length, length };
        way.StrokeDashOffset = length;
        way.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(length, 0, Travel)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        });

        _detourLayer.Children.Add(way);
    }

    /// <summary>
    /// One shortest route, preferring squares the other route already used.
    ///
    /// Ties on a distance field are everywhere, and choosing among them arbitrarily redraws
    /// a route that has not really changed. Keeping to the old one wherever it is still
    /// shortest is the whole of why the difference reads as a detour rather than as a
    /// different board.
    /// </summary>
    private static void TraceFamiliar(in GameState state, int player, Span<byte> distances, UInt128 old, List<int> cells)
    {
        int cell = state.PawnOf(player);
        if (distances[cell] == PathFinder.Unreachable) return;

        cells.Add(cell);

        while (distances[cell] > 0)
        {
            int next = -1;
            bool kept = false;

            for (int direction = 0; direction < 4; direction++)
            {
                if (state.Blocked(cell, direction)) continue;

                int neighbour = cell + Board.Delta[direction];
                byte step = distances[neighbour];

                // On a distance field anything strictly closer is already one step closer,
                // so this is the whole of the test for being on a shortest route.
                if (step == PathFinder.Unreachable || step >= distances[cell]) continue;

                bool familiar = (old & Board.Bit(neighbour)) != 0;

                // Take the first candidate, and after that only trade up: a square the old
                // route used, in place of one it did not.
                if (next >= 0 && !(familiar && !kept)) continue;

                next = neighbour;
                kept = familiar;
            }

            if (next < 0) break;

            cell = next;
            cells.Add(cell);
        }
    }

    /// <summary>
    /// The run of a route the other one does not have, with one square kept at each end so
    /// the two arcs start and finish on the same squares — which is what makes them read as
    /// one route leaving and rejoining rather than as two unrelated lines.
    /// </summary>
    private static List<int> Diverging(List<int> route, UInt128 shared)
    {
        int first = -1;
        int last = -1;

        for (int i = 0; i < route.Count; i++)
        {
            if ((shared & Board.Bit(route[i])) != 0) continue;

            if (first < 0) first = i;
            last = i;
        }

        if (first < 0) return new List<int>();

        int from = Math.Max(first - 1, 0);
        int to = Math.Min(last + 1, route.Count - 1);

        return route.GetRange(from, to - from + 1);
    }

    /// <summary>The leading part of a route, up to the first portal it takes.</summary>
    private static List<int> Unbroken(List<int> route)
    {
        for (int i = 1; i < route.Count; i++)
            if (!AreNeighbours(route[i - 1], route[i]))
                return route.GetRange(0, i);

        return route;
    }

    private PointCollection Centres(List<int> cells)
    {
        var points = new PointCollection(cells.Count);

        foreach (int cell in cells)
            points.Add(new Point(CellCentreOf(Board.ColOf(cell)), CellCentreOf(Board.RowOf(cell))));

        return points;
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
        _detourLayer.Children.Clear();
        WallPreviewChanged?.Invoke(this, null);
    }

    // ================================================================= caret ==

    /// <summary>
    /// Puts the keyboard's caret on a move, or takes it off the board. The whole of the
    /// contract in <see cref="Views.IBoardAim"/>: where the caret may stand and whether what
    /// it stands on is legal are questions about the position, answered by the screen; where
    /// that square actually is on a board that may have been turned round is a question only
    /// the board can answer, and this is it.
    /// </summary>
    public void Aim(Move? at, bool playable)
    {
        if (at is not { } move)
        {
            if (_caretAt is null) return;

            _caretAt = null;
            _caret.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(130)));
            return;
        }

        // Arrows step the caret within one kind of move and W changes the kind, so staying
        // the same kind is exactly the case where the caret is travelling between two of the
        // same thing. It glides then, for the reason the hover highlight does — the eye keeps
        // hold of it. Changing kind swaps a square for a groove, and a bar sliding out of a
        // square would be one shape pretending to be another, so that lands where it is.
        bool travelling = _caretAt is { } previous && previous.Kind == move.Kind;

        _caretAt = move;
        _caretPlayable = playable;

        PlaceCaret(travelling);

        if (!travelling)
            _caret.BeginAnimation(OpacityProperty, new DoubleAnimation(0.85, TimeSpan.FromMilliseconds(130)));
    }

    /// <summary>
    /// Draws the caret where its move actually is. Split from <see cref="Aim"/> so the board
    /// can put it back on the right square after being turned around or reframed, without the
    /// screen having to notice that the geometry moved under it.
    ///
    /// It takes the exact footprint of the move it stands on — the square, or the wall — for
    /// the same reason the ghost does: a caret the size of what you are about to play tells
    /// you what you are about to play. That is also why it is not inflated the way the
    /// last-move mark is; when the two land on one square you get two rings rather than one
    /// ambiguous one.
    /// </summary>
    private void PlaceCaret(bool animate)
    {
        if (_caretAt is not { } move) return;

        bool pawn = move.Kind == MoveKind.Pawn;
        double x, y;

        if (pawn)
        {
            x = CellOriginOf(move.Col);
            y = CellOriginOf(move.Row);
            _caret.Width = _caret.Height = CellSize;
        }
        else
        {
            bool horizontal = move.IsHorizontal;
            x = WallXOf(move.Kind, move.Col);
            y = WallYOf(move.Kind, move.Row);
            _caret.Width = horizontal ? WallLength : GapSize;
            _caret.Height = horizontal ? GapSize : WallLength;
        }

        _caret.RadiusX = _caret.RadiusY = pawn ? 3 : 2;

        // The caret is drawn in the ink of whoever is on move, like the hover highlight and
        // the move hints, because it belongs to the player steering it. Somewhere it cannot
        // go is shown by breaking the line rather than by turning it red: the ghost makes the
        // same choice a few lines up, a warning colour would clash with a player's own ink,
        // and a caret that changed colour on nearly every arrow press would only flicker —
        // most squares on a board are not somewhere a pawn may step.
        _caret.Stroke = Palette.BrushOf(_state.SideToMove == 0 ? Palette.Accent0 : Palette.Accent1);
        _caret.StrokeDashArray = _caretPlayable ? null : [2, 2];

        if (!animate)
        {
            _caret.BeginAnimation(Canvas.LeftProperty, null);
            _caret.BeginAnimation(Canvas.TopProperty, null);
            Place(_caret, x, y);
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(170);

        _caret.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(x, duration) { EasingFunction = ease });
        _caret.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(y, duration) { EasingFunction = ease });
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

        if (!live) _ringOn = -1;

        for (int player = 0; player < 2; player++)
            SetTurnRing(player, live && player == active);

        RefreshReading();
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

        _lastMoveBy = mover;
        ApplyLastMoveInk();

        _lastMoveMark.BeginAnimation(Canvas.LeftProperty, null);
        _lastMoveMark.BeginAnimation(Canvas.TopProperty, null);
        Place(_lastMoveMark, x, y);

        _lastMoveMark.BeginAnimation(OpacityProperty, new DoubleAnimation(0.75, TimeSpan.FromMilliseconds(220)));
    }

    /// <summary>
    /// Outline plus a wash inside it. The outline alone was quiet enough to miss, and the
    /// last move is exactly what you look for after glancing away.
    ///
    /// The outline rides the shared brush and follows the theme by itself. The wash is that
    /// same colour at a fraction of its strength, which has to be a brush of its own — so it
    /// is mixed again on a theme change, or a mark left standing across one keeps the old
    /// palette's ink inside the new palette's outline. It is mixed from the colour the theme
    /// is going to, not from the one the shared brush is currently part-way through fading
    /// toward, so the two arrive together rather than the wash chasing the outline.
    /// </summary>
    private void ApplyLastMoveInk()
    {
        if (_lastMoveBy < 0) return;

        string accent = _lastMoveBy == 0 ? Palette.Accent0 : Palette.Accent1;

        _lastMoveMark.Stroke = Palette.BrushOf(accent);
        _lastMoveMark.Fill = new SolidColorBrush(Palette.ColorOf(accent)) { Opacity = 0.13 };
    }

    private void HideLastMove()
    {
        _lastMoveBy = -1;
        _lastMoveMark.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
    }

    /// <summary>
    /// Works out the whole reading and draws it: the stain on every tile, the seam between
    /// the two reaches, and each player's way home.
    /// </summary>
    private void RefreshReading()
    {
        ReadPosition();
        RefreshCells();
        RefreshFront();
        RefreshRoutes();
    }

    /// <summary>
    /// How many steps every square is from a player's pawn, flooding out the way that pawn
    /// may actually walk. Squares out of play need no test of their own: taking one out
    /// blocks it in all four directions and blocks each of its neighbours back toward it,
    /// so a frontier can neither enter a gap nor leave one — and the ring outside a smaller
    /// game is sealed the same way when the board is built.
    /// </summary>
    private static void FillReach(in GameState state, int player, Span<byte> steps)
    {
        steps.Fill(PathFinder.Unreachable);

        UInt128 reached = Board.Bit(state.PawnOf(player));
        UInt128 previous = 0;
        byte distance = 0;

        while (reached != previous)
        {
            UInt128 layer = reached & ~previous;

            while (layer != 0)
            {
                steps[Board.LowestBit(layer)] = distance;
                layer &= layer - UInt128.One;
            }

            previous = reached;
            reached = PathFinder.Expand(state, reached);
            distance++;
        }
    }

    /// <summary>
    /// Which player reaches each square first, and how far ahead they are there. Held in
    /// <see cref="_owner"/> and <see cref="_stain"/> rather than returned, because the tiles
    /// and the seam are two drawings of one answer.
    /// </summary>
    private void ReadPosition()
    {
        // A finished game has nothing left to read: the race is over, and a board still
        // stained with who was going to get there first would be answering last move's
        // question. The seam and the routes come down for the same reason.
        if (!_reading || _state.IsGameOver)
        {
            Array.Fill(_owner, -1);
            return;
        }

        Span<byte> mine = stackalloc byte[Board.CellCount];
        Span<byte> theirs = stackalloc byte[Board.CellCount];

        FillReach(_state, 0, mine);
        FillReach(_state, 1, theirs);

        for (int cell = 0; cell < Board.CellCount; cell++)
        {
            byte first = mine[cell];
            byte second = theirs[cell];

            // Neither can get there — a pocket the walls closed off, or a square that is
            // not part of this game at all. It belongs to nobody and stays a plain tile.
            if (first == PathFinder.Unreachable && second == PathFinder.Unreachable)
            {
                _owner[cell] = -1;
                continue;
            }

            // A square both pawns reach in the same number of steps goes to whoever is on
            // move, because they take the first of those steps. That is also why the seam
            // shifts by a square as the turn passes: the race has genuinely changed hands,
            // and under a reading you have asked for that is the thing you asked to see.
            _owner[cell] = first == second ? _state.SideToMove : first < second ? 0 : 1;

            int margin = first == PathFinder.Unreachable || second == PathFinder.Unreachable
                ? Decisive
                : Math.Abs(first - second);

            _stain[cell] = FaintestStain +
                (StrongestStain - FaintestStain) * Math.Min(margin, Decisive) / Decisive;
        }
    }

    /// <summary>
    /// The seam, as one geometry: every groove with a different answer on either side of it,
    /// drawn down the middle of the gap and a whole pitch long, so consecutive segments meet
    /// at the corners instead of leaving a nick at every crossing.
    ///
    /// Drawn twice off the one figure — a broad haze that gives the line a place on the
    /// board, and a thin stroke that gives it a position.
    /// </summary>
    private void RefreshFront()
    {
        _frontLayer.Children.Clear();
        if (!_reading || _state.IsGameOver) return;

        int from = Math.Max(0, _framedFrom);
        int last = Board.Size - 1 - from;

        var figure = new StreamGeometry();

        using (StreamGeometryContext draw = figure.Open())
        {
            for (int row = from; row <= last; row++)
            {
                for (int col = from; col <= last; col++)
                {
                    int cell = Board.Index(row, col);
                    if (_owner[cell] < 0) continue;

                    // Both segments are stated in screen space off the two squares they run
                    // between, so a turned board needs no separate case: the groove between
                    // two adjacent model columns is the groove between two adjacent screen
                    // columns, whichever way round they were mapped.
                    if (col < last && Divides(cell, cell + 1))
                    {
                        double x = Origin(Math.Min(ViewIndex(col), ViewIndex(col + 1))) + CellSize + GapSize / 2;
                        double y = Origin(ViewIndex(row)) - GapSize / 2;

                        draw.BeginFigure(new Point(x, y), false, false);
                        draw.LineTo(new Point(x, y + Pitch), true, false);
                    }

                    if (row < last && Divides(cell, cell + Board.Size))
                    {
                        double x = Origin(ViewIndex(col)) - GapSize / 2;
                        double y = Origin(Math.Min(ViewIndex(row), ViewIndex(row + 1))) + CellSize + GapSize / 2;

                        draw.BeginFigure(new Point(x, y), false, false);
                        draw.LineTo(new Point(x + Pitch, y), true, false);
                    }
                }
            }
        }

        figure.Freeze();

        if (figure.IsEmpty()) return;

        _frontLayer.Children.Add(FrontStroke(figure, 10, 0.13));
        _frontLayer.Children.Add(FrontStroke(figure, 1.6, 0.62));

        // It arrives where it now is rather than sliding there. When the turn passes the
        // contested squares change hands and the seam is somewhere else — not on its way
        // there, because it was never travelling.
        foreach (UIElement stroke in _frontLayer.Children)
            stroke.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, ((Path)stroke).Opacity, Settle));
    }

    private static Path FrontStroke(Geometry figure, double thickness, double opacity) => new()
    {
        Data = figure,
        Stroke = Palette.BrushOf(Palette.Front),
        StrokeThickness = thickness,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeStartLineCap = PenLineCap.Round,
        Opacity = opacity,
    };

    /// <summary>Whether the seam runs between a square and the neighbour given.</summary>
    private bool Divides(int cell, int neighbour) =>
        _owner[neighbour] >= 0 && _owner[neighbour] != _owner[cell];

    private void RefreshRoutes()
    {
        _routeLayer.Children.Clear();
        if (!_reading || _state.IsGameOver) return;

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
    }

    /// <summary>Whether two squares are a step apart on the board itself, portals aside.</summary>
    private static bool AreNeighbours(int a, int b) =>
        Math.Abs(Board.RowOf(a) - Board.RowOf(b)) + Math.Abs(Board.ColOf(a) - Board.ColOf(b)) == 1;

    /// <summary>
    /// One unbroken stretch of a player's route. A single point is not a stretch.
    ///
    /// A route is a way home, and a body under a spine reads as one. The dashed hairline
    /// this replaces read as a debug overlay somebody had left switched on — right about
    /// where the route went, wrong about what it was.
    /// </summary>
    private void AddRouteLine(PointCollection points, int player)
    {
        if (points.Count < 2) return;

        string accent = player == 0 ? Palette.Accent0 : Palette.Accent1;

        _routeLayer.Children.Add(Ribbon(points, accent, 9, 0.1));
        _routeLayer.Children.Add(Ribbon(points, accent, 2.25, 0.55));
    }

    private static Polyline Ribbon(PointCollection points, string accent, double thickness, double opacity) => new()
    {
        Points = points,
        Stroke = Palette.BrushOf(accent),
        StrokeThickness = thickness,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        Opacity = opacity,
    };

    /// <summary>
    /// Whose turn it is, marked on the board where the eye already is rather than only in
    /// the panel beside it.
    ///
    /// A ring is switched on and switched off; it is never faded and it never pulses. The
    /// old halo breathed forever, which on a board made of paper is the one mark that never
    /// stops asking to be looked at — and a fade would leave a window in which neither piece
    /// is clearly on move, which is precisely the window a player looks up during.
    /// </summary>
    private void SetTurnRing(int player, bool active)
    {
        Ellipse ring = _pawnRing[player];
        var scale = (ScaleTransform)ring.RenderTransform;

        _pawnShapes[player].Opacity = active ? 1 : 0.72;

        ring.BeginAnimation(OpacityProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = scale.ScaleY = 1;

        if (!active)
        {
            ring.Opacity = 0;
            return;
        }

        // Only the ring that is now on lands, and only once: the turn arriving is an event,
        // and the ring standing there afterwards is a state.
        ring.Opacity = 0.55;

        if (_ringOn == player) return;

        _ringOn = player;

        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };
        var land = new DoubleAnimation(1.28, 1, Settle) { EasingFunction = ease };

        ring.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.55, Settle));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, land);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, land);
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
