namespace Quoridor.Core;

/// <summary>The shape of the board a game is played on.</summary>
public enum BoardLayout
{
    /// <summary>The real game: nine by nine, nothing in the way.</summary>
    Open,

    /// <summary>Four squares out of play, set well apart.</summary>
    Pillars,

    /// <summary>Four squares out of play, arranged around the centre.</summary>
    Diamond,
}

/// <summary>
/// Boards with squares taken out of play.
///
/// A blocked square is sealed on all four sides — including from its neighbours, so no
/// step can enter it either. That is the whole of the implementation: movement, the flood
/// fill and the engine all read the same four block masks that walls write to, so they
/// need no idea that holes exist. What they do need is that the fast wall-legality test in
/// the engine is switched off, because it reasons about walls and borders and a hole is
/// neither; see <c>GameState.HasHoles</c>.
///
/// Every layout is symmetric under a half turn, which maps one player's half onto the
/// other's — so whatever a hole does to your route, it does to theirs.
/// </summary>
public static class Layouts
{
    public static readonly BoardLayout[] All = { BoardLayout.Open, BoardLayout.Pillars, BoardLayout.Diamond };

    /// <summary>The squares out of play, as a cell mask.</summary>
    public static UInt128 Holes(BoardLayout layout) => layout switch
    {
        BoardLayout.Pillars => Mask((2, 2), (2, 6), (6, 2), (6, 6)),
        BoardLayout.Diamond => Mask((2, 4), (4, 2), (4, 6), (6, 4)),
        _ => 0,
    };

    public static string Name(BoardLayout layout) => layout switch
    {
        BoardLayout.Pillars => "Pillars",
        BoardLayout.Diamond => "Diamond",
        _ => "Classic",
    };

    public static string Description(BoardLayout layout) => layout switch
    {
        BoardLayout.Pillars => "Four squares out of play, wide apart. Walls near them go further.",
        BoardLayout.Diamond => "Four squares out of play around the centre, so the middle is a squeeze.",
        _ => "The board as the game is normally played.",
    };

    private static UInt128 Mask(params (int Row, int Col)[] cells)
    {
        UInt128 mask = 0;
        foreach ((int row, int col) in cells) mask |= Board.Bit(Board.Index(row, col));
        return mask;
    }
}
