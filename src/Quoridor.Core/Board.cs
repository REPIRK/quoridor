namespace Quoridor.Core;

/// <summary>
/// Static geometry for a standard 9x9 Quoridor board.
///
/// Cells are numbered row-major: <c>index = row * 9 + col</c>, row 0 is the top row.
/// A set of cells is a bitboard held in a <see cref="UInt128"/> where bit
/// <c>index</c> corresponds to that cell; the top 47 bits are always clear.
///
/// Wall slots live on the 8x8 grid of interior grid points: slot (r, c) is the
/// point between rows r/r+1 and columns c/c+1. A horizontal wall there closes the
/// gap below cells (r,c) and (r,c+1); a vertical wall closes the gap right of
/// cells (r,c) and (r+1,c). Slots are packed into a ulong as <c>r * 8 + c</c>.
/// </summary>
public static class Board
{
    /// <summary>
    /// The width of the grid the whole engine is compiled around. A smaller game is
    /// played on a centred square of this grid rather than on a smaller grid: the ring
    /// around it is simply not part of the board. That keeps every index, shift and mask
    /// a compile-time constant, which is worth more than the handful of unused bits.
    /// </summary>
    public const int Size = 9;

    public const int CellCount = Size * Size;
    public const int SlotSize = Size - 1;
    public const int SlotCount = SlotSize * SlotSize;
    public const int WallsPerPlayer = 10;

    /// <summary>The most walls a player can ever hold, pickups included.</summary>
    public const int MaxWalls = 20;

    /// <summary>Direction ids. Deltas assume row-major indexing.</summary>
    public const int North = 0;
    public const int South = 1;
    public const int West = 2;
    public const int East = 3;

    /// <summary>Cell index offset for each direction, indexed by direction id.</summary>
    public static readonly int[] Delta = { -Size, +Size, -1, +1 };

    /// <summary>The two directions perpendicular to each direction (for side-step jumps).</summary>
    public static readonly int[][] Perpendicular =
    {
        new[] { West, East },   // North
        new[] { West, East },   // South
        new[] { North, South }, // West
        new[] { North, South }, // East
    };

    /// <summary>Every playable cell.</summary>
    public static readonly UInt128 All = (UInt128.One << CellCount) - UInt128.One;

    /// <summary>Row 0 — the goal row for player 0.</summary>
    public static readonly UInt128 TopRow;

    /// <summary>Row 8 — the goal row for player 1.</summary>
    public static readonly UInt128 BottomRow;

    public static readonly UInt128 LeftColumn;
    public static readonly UInt128 RightColumn;

    /// <summary>Each row as a bitboard, so a goal row that is not an edge costs a lookup.</summary>
    public static readonly UInt128[] RowMask = new UInt128[Size];

    /// <summary>Starting cell of each player: player 0 at e1 (bottom), player 1 at e9 (top).</summary>
    public static readonly int[] StartCell = { Index(Size - 1, Size / 2), Index(0, Size / 2) };

    static Board()
    {
        UInt128 left = 0, right = 0;
        for (int i = 0; i < Size; i++)
        {
            left |= Bit(Index(i, 0));
            right |= Bit(Index(i, Size - 1));

            UInt128 row = 0;
            for (int col = 0; col < Size; col++) row |= Bit(Index(i, col));
            RowMask[i] = row;
        }

        TopRow = RowMask[0];
        BottomRow = RowMask[Size - 1];
        LeftColumn = left;
        RightColumn = right;
    }

    public static UInt128 Bit(int cell) => UInt128.One << cell;

    public static int Index(int row, int col) => row * Size + col;

    public static int RowOf(int cell) => cell / Size;

    public static int ColOf(int cell) => cell % Size;

    public static bool InBounds(int row, int col) =>
        (uint)row < Size && (uint)col < Size;

    public static bool SlotInBounds(int row, int col) =>
        (uint)row < SlotSize && (uint)col < SlotSize;

    public static int SlotIndex(int row, int col) => row * SlotSize + col;

    /// <summary>The goal row bitboard the given player must reach.</summary>
    public static UInt128 GoalMask(int player) => player == 0 ? TopRow : BottomRow;

    /// <summary>The goal row index the given player must reach.</summary>
    public static int GoalRow(int player) => player == 0 ? 0 : Size - 1;

    /// <summary>Index of the lowest set bit. Undefined for zero.</summary>
    public static int LowestBit(UInt128 board)
    {
        ulong low = (ulong)board;
        return low != 0
            ? System.Numerics.BitOperations.TrailingZeroCount(low)
            : 64 + System.Numerics.BitOperations.TrailingZeroCount((ulong)(board >> 64));
    }

    public static int PopCount(UInt128 board) =>
        System.Numerics.BitOperations.PopCount((ulong)board) +
        System.Numerics.BitOperations.PopCount((ulong)(board >> 64));
}
