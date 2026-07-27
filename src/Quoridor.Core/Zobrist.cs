namespace Quoridor.Core;

/// <summary>
/// Zobrist hashing for game states. Not used by the current greedy bot, but the
/// hash is maintained incrementally so a transposition table can be dropped into
/// the search later without touching <see cref="GameState"/>.
/// </summary>
public static class Zobrist
{
    public static readonly ulong[,] Pawn = new ulong[2, Board.CellCount];
    public static readonly ulong[,] Wall = new ulong[2, Board.SlotCount];
    public static readonly ulong[,] WallsLeft = new ulong[2, Board.MaxWalls + 1];

    /// <summary>A pickup still lying on a square, by kind. Cleared when it is taken.</summary>
    public static readonly ulong[,] Pickup = new ulong[2, Board.CellCount];

    public static readonly ulong SideToMove;

    static Zobrist()
    {
        // splitmix64 with a fixed seed: identical keys across runs keeps
        // opening books and debugging reproducible.
        ulong state = 0x9E3779B97F4A7C15UL;

        for (int p = 0; p < 2; p++)
        {
            for (int c = 0; c < Board.CellCount; c++) Pawn[p, c] = Next(ref state);
            for (int s = 0; s < Board.SlotCount; s++) Wall[p, s] = Next(ref state);
            for (int w = 0; w <= Board.MaxWalls; w++) WallsLeft[p, w] = Next(ref state);
            for (int c = 0; c < Board.CellCount; c++) Pickup[p, c] = Next(ref state);
        }

        SideToMove = Next(ref state);
    }

    private static ulong Next(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        ulong z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
