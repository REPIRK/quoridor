namespace Quoridor.Core;

public enum PickupKind
{
    /// <summary>A spare wall, added to whoever steps on it.</summary>
    Wall,

    /// <summary>A free move: the turn does not pass, so you move again.</summary>
    Skip,
}

/// <summary>The board and the built position, so the view can draw what was generated.</summary>
public readonly record struct BuiltBoard(GameState State, UInt128 Holes);

/// <summary>
/// How much of the setup a player wants to be asked about. Someone who just wants a
/// game should not have to read past every knob to find the button.
/// </summary>
public enum GameFlavour
{
    /// <summary>The plain game, started without further questions.</summary>
    Standard,

    /// <summary>Board, walls, holes, pickups and who moves first, all rolled.</summary>
    Random,

    /// <summary>Every setting exposed.</summary>
    Custom,
}

/// <summary>
/// Everything that varies between games: how big the board is, how many walls each
/// player starts with, how many squares are taken out of play, and how many pickups are
/// scattered over it.
///
/// Holes and pickups are placed at random but always in pairs that map onto each other
/// under a half turn of the board — the same turn that maps one player's half onto the
/// other's. So whatever the roll does to your route it does to your opponent's, and a
/// random board is still a fair one.
/// </summary>
public sealed record GameSetup
{
    /// <summary>Width of the game, on the centred square of the 9-wide grid. Odd, 3..9.</summary>
    public int Size { get; init; } = Board.Size;

    public int Walls { get; init; } = Board.WallsPerPlayer;

    /// <summary>How many squares to take out of play. Rounded down to a pair.</summary>
    public int Holes { get; init; }

    /// <summary>How many pickups to scatter. Rounded down to a pair.</summary>
    public int Pickups { get; init; }

    /// <summary>
    /// How many portals to place, counted in portals rather than in squares — deliberately
    /// unlike the holes and the pickups, which are counted in squares and rounded down to
    /// a pair. A portal's two mouths are one object: there is no such thing as half of it,
    /// so asking for one is asking for one, not for none.
    /// </summary>
    public int Portals { get; init; }

    /// <summary>Fixes the roll, so the same game can be built twice — as it must be
    /// over a network link, where both sides build it from the same numbers.</summary>
    public int Seed { get; init; }

    public static GameSetup Standard { get; } = new();

    /// <summary>
    /// A game with everything rolled. Lives here rather than in either menu because both
    /// builds offer it and a roll that differed between them would not be the same game.
    ///
    /// The size is drawn first and the rest is scaled to it. The numbers that make a
    /// lively nine make a five unplayable: a five has a quarter of the wall slots, and
    /// its two back rows — which no hole may take, or a player could be left with nowhere
    /// to arrive — are already half of its squares.
    /// </summary>
    public static GameSetup Roll(int seed)
    {
        var random = new Random(seed);

        // Nine is the game as people know it and stays the common draw. Five is over in
        // a dozen moves, which is a change of pace rather than an equal third of them.
        int size = random.Next(8) switch
        {
            0 => 5,
            1 or 2 => 7,
            _ => Board.Size,
        };

        int walls = size switch
        {
            5 => random.Next(1, 5),
            7 => random.Next(3, 9),
            _ => random.Next(4, 13),
        };

        // Nothing at all is a common roll for both: a board that is merely a different
        // shape is a game in its own right, and every roll carrying a gimmick would make
        // the plain one feel like the thing that went wrong.
        int holes = Draw(random, size switch
        {
            5 => new[] { 0, 0, 0, 2, 2 },
            7 => new[] { 0, 0, 2, 4, 4 },
            _ => new[] { 0, 0, 2, 4, 6 },
        });

        int pickups = Draw(random, size switch
        {
            5 => new[] { 0, 2, 2, 4, 4 },
            7 => new[] { 0, 4, 4, 6, 6 },
            _ => new[] { 0, 4, 4, 6, 10 },
        });

        // Drawn last, and that is the whole reason the draw is safe to add: every number
        // above is asked of the same Random in the same order it always was, so every seed
        // that ever built a board still builds that board and only this one term is new.
        // A five draws nothing at all, because a five can carry no portal — its two goal
        // rows and the rows beside them are the whole board, and a mouth may sit on none
        // of them. Leading zeros for the same reason as the tables above: a rolled board
        // that is merely a different shape is a game in its own right.
        int portals = size switch
        {
            5 => 0,
            7 => Draw(random, new[] { 0, 0, 0, 1 }),
            _ => Draw(random, new[] { 0, 0, 1, 1, 2 }),
        };

        return new GameSetup
        {
            Size = size,
            Walls = walls,
            Holes = holes,
            Pickups = pickups,
            Portals = portals,
            Seed = seed,
        };
    }

    private static int Draw(Random random, int[] table) => table[random.Next(table.Length)];

    /// <summary>
    /// The numbers, for sending over a link so both sides build the same board.
    ///
    /// The sixth term is sent only when there is a portal to name. A build that predates
    /// portals reads a fixed five terms and rejects anything else, so a portal game does
    /// need a new build at both ends — but a game without portals encodes to the same
    /// string it always did, and goes on being playable against every build ever shipped.
    /// The cost of the feature falls only on the games that use it.
    /// </summary>
    public string Encode()
    {
        string numbers = $"{Size}|{Walls}|{Holes}|{Pickups}|{Seed}";
        return Portals > 0 ? $"{numbers}|{Portals}" : numbers;
    }

    public static bool TryDecode(ReadOnlySpan<string> terms, out GameSetup setup)
    {
        setup = Standard;

        // Five is a board from any build; six is one with portals on it.
        if (terms.Length is not (5 or 6)) return false;

        if (!int.TryParse(terms[0], out int size) || (size & 1) == 0 || size < 3 || size > Board.Size) return false;
        if (!int.TryParse(terms[1], out int walls) || walls < 0 || walls > Board.MaxWalls) return false;
        if (!int.TryParse(terms[2], out int holes) || holes < 0 || holes > Board.CellCount) return false;
        if (!int.TryParse(terms[3], out int pickups) || pickups < 0 || pickups > Board.CellCount) return false;
        if (!int.TryParse(terms[4], out int seed)) return false;

        // The cap is part of the format rather than something to clamp quietly: a term
        // this side cannot honour means the two builds do not agree about the board, and
        // silently playing a different one is the failure the whole handshake exists to
        // prevent. Zero when the term is absent, which is what an old build meant by it.
        int portals = 0;
        if (terms.Length == 6 &&
            (!int.TryParse(terms[5], out portals) || portals < 0 || portals > MaxPortals))
        {
            return false;
        }

        setup = new GameSetup
        {
            Size = size, Walls = walls, Holes = holes, Pickups = pickups, Portals = portals, Seed = seed,
        };

        return true;
    }

    /// <summary>
    /// Asked in terms of what the board will really carry, not of what was requested. A
    /// setup that asks for one hole gets none — holes are placed in mirrored pairs — and
    /// a title reading "classic · 1 hole" would be describing a board that is not there.
    /// </summary>
    public bool IsStandard =>
        Size == Board.Size && Walls == Board.WallsPerPlayer &&
        ActualHoles == 0 && ActualPickups == 0 && ActualPortals == 0;

    /// <summary>
    /// How many squares a hole or a pickup may actually be drawn onto — which is not the
    /// playable area. The two goal rows are reserved, so that a player can never be denied
    /// every square they are aiming for, and both pawns stand on one of them; and the
    /// centre square is its own image under the half turn, so it can never be half of a
    /// mirrored pair. On a nine that leaves 62 of 81 squares, but on a five it leaves 14
    /// of 25, which is why sizing anything against <c>Size * Size</c> was survivable on the
    /// big board and not on the small one.
    ///
    /// Holes and pickups draw from this one supply, holes first, which is why the two
    /// counts below are not independent of each other.
    /// </summary>
    public int DrawableCells => Size * (Size - 2) - 1;

    /// <summary>
    /// How many holes the board will really carry: a whole number of mirrored pairs, and
    /// never more squares than there are to put them on.
    /// </summary>
    public int ActualHoles => Math.Min(Holes & ~1, DrawableCells);

    /// <summary>How many pickups will really be placed, on the squares the holes left.</summary>
    public int ActualPickups => Math.Min(Pickups & ~1, DrawableCells - ActualHoles);

    /// <summary>
    /// How many portals the size can carry, which is a much harder rule than the one the
    /// holes and pickups answer to and has nothing to do with how many squares are spare.
    /// A mouth may not stand on a goal row, on a row beside one, or on the centre row —
    /// so the rows it may stand on number <c>Size - 5</c>: four on a nine, two on a seven,
    /// and none at all on a five, where the goal rows and their neighbours are the whole
    /// board. The seven's two rows are one mirrored pair, and two portals sharing a
    /// pairing are one objective rather than two, so a seven is offered one.
    /// </summary>
    public static int[] PortalOptions(int size) => size switch
    {
        5 => new[] { 0 },
        7 => new[] { 0, 1 },
        _ => new[] { 0, 1, 2 },
    };

    /// <summary>The most portals any board can hold, and so the most the wire may name.</summary>
    public const int MaxPortals = 2;

    /// <summary>How many portals the board will really carry, capped by what the size allows.</summary>
    public int ActualPortals => Math.Clamp(Portals, 0, PortalOptions(Size)[^1]);

    /// <summary>
    /// The wall supplies a size is offered. One list rather than three copies in three
    /// setup screens, because a number offered in one build and not another is a game that
    /// cannot be started from a link. A five has a quarter of the nine's wall slots, so
    /// the nine's generous end is not a longer game there, it is a sealed one.
    /// </summary>
    public static int[] WallOptions(int size) => size switch
    {
        5 => new[] { 0, 1, 2, 3, 4, 5 },
        7 => new[] { 0, 2, 3, 5, 7, 10 },
        _ => new[] { 0, 3, 5, 7, 10, 14, 20 },
    };

    /// <summary>
    /// The hole counts a size is offered, capped by <see cref="DrawableCells"/> for that
    /// size rather than by what reads well in a dropdown.
    /// </summary>
    public static int[] HoleOptions(int size) =>
        size == 5 ? new[] { 0, 2, 4 } : new[] { 0, 2, 4, 6, 10 };

    /// <summary>The pickup counts a size is offered. Same supply as the holes.</summary>
    public static int[] PickupOptions(int size) =>
        size == 5 ? new[] { 0, 2, 4 } : new[] { 0, 4, 6, 10 };

    /// <summary>A short human description, for a title bar.</summary>
    public string Describe()
    {
        var parts = new List<string>(5);

        if (Size != Board.Size) parts.Add($"{Size}×{Size}");
        if (Walls != Board.WallsPerPlayer) parts.Add(Walls == 1 ? "1 wall" : $"{Walls} walls");

        // The counts the build will honour, not the ones asked for. Naming a number the
        // board never carried is worse than naming none: it reads as a rule the player
        // has misunderstood rather than as a request the board could not meet.
        if (ActualHoles > 0) parts.Add($"{ActualHoles} holes");
        if (ActualPickups > 0) parts.Add($"{ActualPickups} pickups");
        if (ActualPortals > 0) parts.Add(ActualPortals == 1 ? "1 portal" : $"{ActualPortals} portals");

        return parts.Count == 0 ? "classic" : string.Join(" · ", parts);
    }

    /// <summary>
    /// Builds the starting position. Holes are re-rolled until both players have a route
    /// and neither is walled into their own start; a few dozen squares and a handful of
    /// holes make that overwhelmingly likely first time, and the retry is only there so
    /// an unlucky roll cannot produce an unplayable game.
    /// </summary>
    public BuiltBoard Build()
    {
        var random = new Random(Seed);

        // Halved after every run of failed attempts, so a hole count this board cannot
        // survive degrades into the same game scattered more thinly. It used to fall back
        // to a plain board, which threw away the pickups as well — and the pickups are
        // placed after the check, so they were never what failed it. Zero holes always
        // leaves both routes open, so the halving is also what makes this terminate.
        int holeBudget = ActualHoles;

        for (int attempt = 1; ; attempt++)
        {
            GameState state = GameState.CreateInitial(Size, Walls);
            UInt128 holes = 0;

            // Squares that must stay clear: both pawns, and the two goal rows, so a
            // player can never be denied every square they are aiming for. Already absent
            // from the drawable list; kept because TakePair works on any list.
            UInt128 reserved =
                Board.Bit(state.PawnOf(0)) | Board.Bit(state.PawnOf(1)) |
                state.GoalMask(0) | state.GoalMask(1);

            List<int> free = DrawableSquares(state);

            for (int i = 0; i < holeBudget; i += 2)
            {
                int cell = TakePair(random, free, reserved | holes, out int mirror);
                if (cell < 0) break;

                holes |= Board.Bit(cell) | Board.Bit(mirror);
            }

            for (int cell = 0; cell < Board.CellCount; cell++)
                if ((holes & Board.Bit(cell)) != 0)
                    state.SealCell(cell);

            if (!PathFinder.HasPath(state, 0) || !PathFinder.HasPath(state, 1))
            {
                // Six failures in a row means the number of holes is unplayable in itself
                // rather than the roll being unlucky, so ask for fewer and carry on.
                if (attempt % 6 == 0) holeBudget = (holeBudget / 2) & ~1;
                continue;
            }

            // Pickups go where players actually walk. A free move is worth a whole tempo,
            // but only if you were going that way anyway: two squares off your route
            // costs two moves to fetch one, and the prize is a loss. Scattered evenly
            // over the board that is what most of them were. Weighted toward the middle
            // files they become something both players pass, and therefore contest.
            List<int> lanes = NearTheMiddle(state, free);

            for (int i = 0; i < ActualPickups; i += 2)
            {
                // A bias, not a rule. Three in four go where the walking is, which is
                // what makes them worth taking; the rest are scattered, so the edges of
                // the board are not dead ground and no two games look the same.
                bool inLane = random.Next(4) != 0;

                int cell = inLane
                    ? TakePair(random, lanes, reserved | holes, out int mirror)
                    : TakePair(random, free, reserved | holes, out mirror);

                if (cell < 0) cell = TakePair(random, free, reserved | holes, out mirror);
                if (cell < 0) break;

                // Whichever list it came from, it is gone from both.
                free.Remove(cell);
                free.Remove(mirror);
                lanes.Remove(cell);
                lanes.Remove(mirror);

                // Both squares of a pair carry the same kind, or the mirror would not
                // be a mirror. Walls are the commoner of the two: a free move is the
                // stronger prize and wants to be the rarer one.
                PickupKind kind = random.Next(3) == 0 ? PickupKind.Skip : PickupKind.Wall;

                state.PlacePickup(cell, kind);
                state.PlacePickup(mirror, kind);
            }

            PlacePortals(random, ref state, free, reserved | holes);

            return new BuiltBoard(state, holes);
        }
    }

    /// <summary>
    /// Links pairs of squares, after everything else has been placed and from what the
    /// holes and pickups left. Nothing at all happens unless a portal was asked for, so a
    /// board without them draws no number and builds exactly as it built before portals
    /// existed — which is what keeps every seed ever shared pointing at the same game.
    ///
    /// A mouth is drawn on its own merits and nothing tries to put it where the walking
    /// is. That bias belongs to the pickups: a pickup you have to leave your route for is
    /// a tax rather than a prize, whereas the detour to reach a mouth is the entire cost
    /// of using the portal, and the whole decision the feature is asking for. Bias the
    /// mouths toward the middle files and every portal game becomes the same game.
    /// </summary>
    private void PlacePortals(Random random, ref GameState state, List<int> free, UInt128 taken)
    {
        int wanted = ActualPortals;
        if (wanted == 0) return;

        int origin = state.GoalRow(0);
        int last = state.GoalRow(1);
        int centre = (origin + last) / 2;

        // Reachability on the board as built so far, which is what rules out a mouth that
        // the holes have ringed. Portals only ever add edges, so a square reachable now is
        // reachable once they are placed and this fill never has to be taken again.
        Span<byte> reach = stackalloc byte[Board.CellCount];
        PathFinder.FillDistancesToGoal(state, 0, reach);

        var mouths = new List<int>(free.Count);

        foreach (int cell in free)
        {
            int row = Board.RowOf(cell), mirror = Mirror(cell);

            // Not a goal row and not beside one: a nine's portal from row 1 to row 7 would
            // save six rows of a seven-row journey, permanently, in every game played on
            // that board. Not the centre row either, where a mouth's mirror is in the same
            // row — a portal that moves you sideways, in a game whose goal is a whole row.
            if (row <= origin + 1 || row >= last - 1 || row == centre) continue;

            // Four apart, so the two mouths share no neighbour. Rows 3 and 5 of one file
            // both touch the square between them, and a portal between them would let the
            // occupied-mouth case offer a square the ordinary four directions already
            // offered — a duplicate move is a subtree searched twice at every node.
            if (Math.Abs(row - Board.RowOf(mirror)) +
                Math.Abs(Board.ColOf(cell) - Board.ColOf(mirror)) < 4)
            {
                continue;
            }

            if (reach[cell] == PathFinder.Unreachable || reach[mirror] == PathFinder.Unreachable)
                continue;

            mouths.Add(cell);
        }

        UInt128 used = taken | state.WallPickups | state.SkipPickups;
        int placed = 0;

        for (int i = 0; i < wanted && mouths.Count > 0; i++)
        {
            int cell = TakePair(random, mouths, used, out int mirror);
            if (cell < 0) break;

            // The draw is spent either way — the pair is out of the bag — so a rejection
            // costs this one candidate and not the portal. Failing to find a second one
            // that stands clear of the first is not an error; one portal is a game.
            if (placed > 0 && !ColumnsApart(state, cell)) { i--; continue; }

            used |= Board.Bit(cell) | Board.Bit(mirror);
            state.PlacePortal(cell);
            placed++;
        }
    }

    /// <summary>
    /// Whether a portal drawn here would stand clear of the ones already placed. Every
    /// mouth is compared against both of the new ones, because a mouth is a place a player
    /// walks to: two pairs whose mouths sit in neighbouring files are one objective in two
    /// colours, and the second portal has bought the board nothing.
    /// </summary>
    private static bool ColumnsApart(in GameState state, int cell)
    {
        UInt128 mouths = state.PortalMouths();
        int here = Board.ColOf(cell), there = Board.ColOf(Mirror(cell));

        while (mouths != 0)
        {
            int at = Board.LowestBit(mouths);
            mouths &= mouths - UInt128.One;

            int col = Board.ColOf(at);
            if (Math.Abs(col - here) < 2 || Math.Abs(col - there) < 2) return false;
        }

        return true;
    }

    /// <summary>
    /// The squares near the middle files, where both players' routes run. Both start on
    /// the centre file and the shortest way home is straight up it, so a pickup within a
    /// square or two of that line is one you can take without leaving your route — which
    /// is the difference between a prize and a tax.
    /// </summary>
    private static List<int> NearTheMiddle(in GameState state, List<int> free)
    {
        int origin = state.GoalRow(0);
        int last = state.GoalRow(1);
        int centre = (origin + last) / 2;

        var lanes = new List<int>(free.Count);

        foreach (int cell in free)
            if (Math.Abs(Board.ColOf(cell) - centre) <= 2)
                lanes.Add(cell);

        return lanes;
    }

    /// <summary>
    /// Every square a hole or a pickup may be drawn onto: the game's own square, less the
    /// two goal rows and less the centre.
    ///
    /// Removing them here rather than leaving them for <see cref="TakePair"/> to reject is
    /// not tidiness. TakePair draws at random and skips what it may not take, without ever
    /// removing it, so the reserved squares stay in the bag for every draw. On a five they
    /// are two fifths of the board and it spent its whole retry budget rejecting them: a
    /// five asked for ten pickups placed eight, or four, and said nothing.
    /// </summary>
    private static List<int> DrawableSquares(in GameState state)
    {
        int origin = state.GoalRow(0);
        int last = state.GoalRow(1);
        int centre = Board.CellCount / 2;

        var cells = new List<int>((last - origin - 1) * (last - origin + 1));

        for (int row = origin + 1; row < last; row++)
        {
            for (int col = origin; col <= last; col++)
            {
                int cell = Board.Index(row, col);

                // Its own image under the half turn, so it can never be half of a pair.
                if (cell != centre) cells.Add(cell);
            }
        }

        return cells;
    }

    /// <summary>
    /// Draws a square and its opposite number under a half turn, skipping anything
    /// reserved or already used. Returns -1 when the board has run out of room.
    /// </summary>
    private static int TakePair(Random random, List<int> free, UInt128 taken, out int mirror)
    {
        for (int tries = 0; tries < 64 && free.Count > 0; tries++)
        {
            int at = random.Next(free.Count);
            int cell = free[at];

            mirror = Mirror(cell);

            if (cell == mirror || (taken & (Board.Bit(cell) | Board.Bit(mirror))) != 0) continue;

            free.RemoveAt(at);
            free.Remove(mirror);

            return cell;
        }

        mirror = -1;
        return -1;
    }

    /// <summary>The square a half turn of the grid maps this one onto.</summary>
    private static int Mirror(int cell) => Board.CellCount - 1 - cell;
}
