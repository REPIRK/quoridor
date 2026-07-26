using System.Text;

namespace Quoridor.Core;

/// <summary>
/// Standard Quoridor notation: files a-i run left to right, ranks 1-9 run bottom
/// to top, so player 0 starts on e1. Walls are named by their slot's lower-left
/// grid point plus an orientation letter, e.g. <c>e3h</c>.
/// </summary>
public static class Notation
{
    public static string Format(Move move)
    {
        if (move.Kind == MoveKind.Pawn)
            return $"{(char)('a' + move.Col)}{Board.Size - move.Row}";

        char file = (char)('a' + move.Col);
        int rank = Board.SlotSize - move.Row;
        return $"{file}{rank}{(move.IsHorizontal ? 'h' : 'v')}";
    }

    public static bool TryParse(string text, out Move move)
    {
        move = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        text = text.Trim().ToLowerInvariant();

        char file = text[0];
        if (file < 'a' || file > 'i') return false;
        int col = file - 'a';

        if (text.Length < 2 || !char.IsDigit(text[1])) return false;
        int rank = text[1] - '0';

        if (text.Length == 2)
        {
            int row = Board.Size - rank;
            if (!Board.InBounds(row, col)) return false;
            move = Move.Pawn(row, col);
            return true;
        }

        if (text.Length != 3) return false;

        int slotRow = Board.SlotSize - rank;
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
