using System.Text;

namespace Quoridor.Core;

/// <summary>
/// Standard Quoridor notation: files a-i run left to right, ranks 1-9 run bottom
/// to top, so player 0 starts on e1. Walls are named by their slot's lower-left
/// grid point plus an orientation letter, e.g. <c>e3h</c>.
///
/// A smaller game is played on a centred square of the same grid, and its squares are
/// named from its own corner rather than the grid's — a 7x7 board runs a1 to g7, not
/// b2 to h8. That is what the board's own margin prints, so <paramref name="origin"/>
/// has to be passed anywhere the two are shown together. It is the same number for
/// both sides of a network game, which agree the board before a move is ever sent.
/// </summary>
public static class Notation
{
    public static string Format(Move move, int origin = 0)
    {
        int last = Board.Size - 1 - origin;

        if (move.Kind == MoveKind.Pawn)
            return $"{(char)('a' + move.Col - origin)}{last - move.Row + 1}";

        char file = (char)('a' + move.Col - origin);
        int rank = last - move.Row;
        return $"{file}{rank}{(move.IsHorizontal ? 'h' : 'v')}";
    }

    /// <summary>The move as the given position would name it.</summary>
    public static string Format(Move move, in GameState state) => Format(move, state.GoalRow(0));

    public static bool TryParse(string text, out Move move, int origin = 0)
    {
        move = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        text = text.Trim().ToLowerInvariant();

        char file = text[0];
        if (file < 'a' || file > 'i') return false;
        int col = file - 'a' + origin;

        if (text.Length < 2 || !char.IsDigit(text[1])) return false;
        int rank = text[1] - '0';

        int last = Board.Size - 1 - origin;

        if (text.Length == 2)
        {
            int row = last - rank + 1;
            if (!Board.InBounds(row, col)) return false;
            move = Move.Pawn(row, col);
            return true;
        }

        if (text.Length != 3) return false;

        int slotRow = last - rank;
        if (!Board.SlotInBounds(slotRow, col)) return false;

        move = text[2] switch
        {
            'h' => new Move(MoveKind.HorizontalWall, slotRow, col),
            'v' => new Move(MoveKind.VerticalWall, slotRow, col),
            _ => default,
        };

        return move.IsWall;
    }

    /// <summary>Renders a position as text. Used by the self-tests and for debugging.</summary>
    public static string Render(in GameState state)
    {
        var sb = new StringBuilder();

        for (int row = 0; row < Board.Size; row++)
        {
            for (int col = 0; col < Board.Size; col++)
            {
                int cell = Board.Index(row, col);
                char glyph = '.';
                if (state.PawnOf(0) == cell) glyph = '1';
                else if (state.PawnOf(1) == cell) glyph = '2';

                sb.Append(glyph);
                if (col < Board.Size - 1)
                    sb.Append(state.Blocked(cell, Board.East) ? '|' : ' ');
            }

            sb.AppendLine();

            if (row < Board.Size - 1)
            {
                for (int col = 0; col < Board.Size; col++)
                {
                    sb.Append(state.Blocked(Board.Index(row, col), Board.South) ? '-' : ' ');
                    if (col < Board.Size - 1) sb.Append(' ');
                }

                sb.AppendLine();
            }
        }

        sb.AppendLine($"walls: P1={state.WallsOf(0)} P2={state.WallsOf(1)}  turn=P{state.SideToMove + 1}");
        return sb.ToString();
    }
}
