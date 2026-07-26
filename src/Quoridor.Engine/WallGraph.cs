using Quoridor.Core;

namespace Quoridor.Engine;

/// <summary>
/// Decides whether a wall placement is even capable of sealing a player in.
///
/// Think of walls as edges in a graph whose vertices are the 10x10 grid points of
/// the board. Adding a wall can only cut the board in two if that wall becomes part
/// of an unbroken chain running from one border to another. Such a chain has to
/// enter the new wall at one of its grid points and leave at another, so at least
/// two of the wall's three points — both ends and the middle — must already touch
/// something: the border, or another wall.
///
/// The middle point matters and is easy to overlook: a wall spans two cells, so a
/// perpendicular wall can attach halfway along it, not only at its tips.
///
/// The condition is necessary, so failing it is a definitive "cannot disconnect".
/// The pay-off is large: wall legality otherwise costs two flood fills, and this
/// test is a handful of bit reads. Through the opening and most of the middlegame
/// the majority of candidate walls float free and skip the fills entirely.
/// </summary>
internal static class WallGraph
{
    /// <summary>
    /// True when the placement might disconnect the board and therefore still needs
    /// the full path check. False means it provably cannot.
    /// </summary>
    public static bool CanDisconnect(in GameState state, MoveKind kind, int row, int col)
    {
        // A square taken out of play is a boundary this argument cannot see: a chain can
        // run from a border to a hole and cut the board without two of the wall's points
        // touching anything. On those boards the shortcut is simply not taken.
        if (state.HasHoles) return true;

        // A horizontal wall at slot (r,c) runs from grid point (r+1, c) through
        // (r+1, c+1) to (r+1, c+2); a vertical one from (r, c+1) through (r+1, c+1)
        // to (r+2, c+1).
        int anchors = kind == MoveKind.HorizontalWall
            ? Count(state, row + 1, col, row + 1, col + 1, row + 1, col + 2)
            : Count(state, row, col + 1, row + 1, col + 1, row + 2, col + 1);

        return anchors >= 2;
    }

    private static int Count(in GameState state, int i1, int j1, int i2, int j2, int i3, int j3)
    {
        int anchors = 0;
        if (IsAnchored(state, i1, j1)) anchors++;
        if (IsAnchored(state, i2, j2)) anchors++;
        if (IsAnchored(state, i3, j3)) anchors++;
        return anchors;
    }

    /// <summary>Whether grid point (i, j) lies on the border or on an existing wall.</summary>
    private static bool IsAnchored(in GameState state, int i, int j)
    {
        if (i == 0 || i == Board.Size || j == 0 || j == Board.Size) return true;

        // Interior grid point (i, j) is the centre of wall slot (i-1, j-1).
        int slotRow = i - 1;
        int slotCol = j - 1;

        // Horizontal walls centred here, or ending here from either side.
        if (HasHorizontal(state, slotRow, slotCol - 1) ||
            HasHorizontal(state, slotRow, slotCol) ||
            HasHorizontal(state, slotRow, slotCol + 1))
        {
            return true;
        }

        // Vertical walls centred here, or ending here from above or below.
        return HasVertical(state, slotRow - 1, slotCol) ||
               HasVertical(state, slotRow, slotCol) ||
               HasVertical(state, slotRow + 1, slotCol);
    }

    private static bool HasHorizontal(in GameState state, int row, int col) =>
        Board.SlotInBounds(row, col) && (state.HorizontalWalls & (1UL << Board.SlotIndex(row, col))) != 0;

    private static bool HasVertical(in GameState state, int row, int col) =>
        Board.SlotInBounds(row, col) && (state.VerticalWalls & (1UL << Board.SlotIndex(row, col))) != 0;
}
