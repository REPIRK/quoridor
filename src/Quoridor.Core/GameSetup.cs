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

        return new GameSetup
        {
            Size = size,
            Walls = walls,
            Holes = holes,
            Pickups = pickups,
            Seed = seed,
        };
    }

    private static int Draw(Random random, int[] table) => table[random.Next(table.Length)];

    /// <summary>The numbers, for sending over a link so both sides build the same board.</summary>
    public string Encode() => $"{Size}|{Walls}|{Holes}|{Pickups}|{Seed}";

    public static bool TryDecode(ReadOnlySpan<string> terms, out GameSetup setup)
    {
        setup = Standard;

        if (terms.Length != 5) return false;

        if (!int.TryParse(terms[0], out int size) || (size & 1) == 0 || size < 3 || size > Board.Size) return false;
        if (!int.TryParse(terms[1], out int walls) || walls < 0 || walls > Board.MaxWalls) return false;
        if (!int.TryParse(terms[2], out int holes) || holes < 0 || holes > Board.CellCount) return false;
        if (!int.TryParse(terms[3], out int pickups) || pickups < 0 || pickups > Board.CellCount) return false;
        if (!int.TryParse(terms[4], out int seed)) return false;

        setup = new GameSetup { Size = size, Walls = walls, Holes = holes, Pickups = pickups, Seed = seed };
        return true;
    }

    public bool IsStandard =>
        Size == Board.Size && Walls == Board.WallsPerPlayer && Holes == 0 && Pickups == 0;

    /// <summary>How many squares are in the game at all, before holes.</summary>
    public int PlayableCells => Size * Size;

    /// <summary>A short human description, for a title bar.</summary>
    public string Describe()
    {
        var parts = new List<string>(3);

        if (Size != Board.Size) parts.Add($"{Size}×{Size}");
        if (Walls != Board.WallsPerPlayer) parts.Add(Walls == 1 ? "1 wall" : $"{Walls} walls");
        if (Holes > 0) parts.Add($"{Holes} holes");
        if (Pickups > 0) parts.Add($"{Pickups} pickups");

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

        for (int attempt = 0; ; attempt++)
        {
            GameState state = GameState.CreateInitial(Size, Walls);
            UInt128 holes = 0;

            // Squares that must stay clear: both pawns, and the two goal rows, so a
            // player can never be denied every square they are aiming for.
            UInt128 reserved =
                Board.Bit(state.PawnOf(0)) | Board.Bit(state.PawnOf(1)) |
                state.GoalMask(0) | state.GoalMask(1);

            List<int> free = PlayableSquares(state);

            int holeCount = Math.Min(Holes & ~1, free.Count - 2);
            for (int i = 0; i < holeCount; i += 2)
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
                // Six failures means the numbers themselves are unplayable, not the roll.
                if (attempt < 6) continue;
                return new BuiltBoard(GameState.CreateInitial(Size, Walls), 0);
            }

            // Pickups go where players actually walk. A free move is worth a whole tempo,
            // but only if you were going that way anyway: two squares off your route
            // costs two moves to fetch one, and the prize is a loss. Scattered evenly
            // over the board that is what most of them were. Weighted toward the middle
            // files they become something both players pass, and therefore contest.
            List<int> lanes = NearTheMiddle(state, free);

            int pickupCount = Math.Min(Pickups & ~1, free.Count);
            for (int i = 0; i < pickupCount; i += 2)
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

            return new BuiltBoard(state, holes);
        }
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

    /// <summary>Every square in the game, ignoring what is on it.</summary>
    private List<int> PlayableSquares(in GameState state)
    {
        int origin = state.GoalRow(0);
        int last = state.GoalRow(1);

        var cells = new List<int>(Size * Size);

        for (int row = origin; row <= last; row++)
            for (int col = origin; col <= last; col++)
                cells.Add(Board.Index(row, col));

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
