namespace Quoridor.Core;

public enum MoveKind : byte
{
    /// <summary>Step the moving player's pawn to (Row, Col).</summary>
    Pawn = 0,

    /// <summary>Place a horizontal wall at slot (Row, Col).</summary>
    HorizontalWall = 1,

    /// <summary>Place a vertical wall at slot (Row, Col).</summary>
    VerticalWall = 2,
}

/// <summary>
/// A single Quoridor action. Four bytes wide so move buffers stay on the stack
/// during search.
/// </summary>
public readonly struct Move : IEquatable<Move>
{
    public readonly MoveKind Kind;
    public readonly byte Row;
    public readonly byte Col;

    public Move(MoveKind kind, int row, int col)
    {
        Kind = kind;
        Row = (byte)row;
        Col = (byte)col;
    }

    public static Move ToCell(int cell) => new(MoveKind.Pawn, Board.RowOf(cell), Board.ColOf(cell));

    public static Move Pawn(int row, int col) => new(MoveKind.Pawn, row, col);

    public static Move Wall(bool horizontal, int row, int col) =>
        new(horizontal ? MoveKind.HorizontalWall : MoveKind.VerticalWall, row, col);

    public bool IsWall => Kind != MoveKind.Pawn;

    public bool IsHorizontal => Kind == MoveKind.HorizontalWall;

    /// <summary>Target cell index. Only meaningful for <see cref="MoveKind.Pawn"/>.</summary>
    public int Cell => Board.Index(Row, Col);

    /// <summary>Wall slot index. Only meaningful for wall moves.</summary>
    public int Slot => Board.SlotIndex(Row, Col);

    public bool Equals(Move other) => Kind == other.Kind && Row == other.Row && Col == other.Col;

    public override bool Equals(object? obj) => obj is Move other && Equals(other);

    public override int GetHashCode() => ((int)Kind << 16) | (Row << 8) | Col;

    public static bool operator ==(Move a, Move b) => a.Equals(b);

    public static bool operator !=(Move a, Move b) => !a.Equals(b);

    public override string ToString() => Notation.Format(this);
}
