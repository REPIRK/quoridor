using System.Runtime.CompilerServices;

namespace Quoridor.Core;

/// <summary>
/// A complete Quoridor position. Deliberately a mutable struct of ~96 bytes: the
/// search copies it (<c>var next = state; next.Apply(move);</c>) instead of
/// implementing undo, which removes a whole class of bugs and still costs less
/// than a cache line pair per node.
///
/// The four <c>Blocked*</c> bitboards answer "may a pawn on this cell step in
/// this direction" in one AND. Board edges are baked into them at construction,
/// so movement and flood fill never need bounds checks.
/// </summary>
public struct GameState
{
    /// <summary>Cells from which a step north is impossible (wall or edge).</summary>
    public UInt128 BlockedNorth;

    public UInt128 BlockedSouth;
    public UInt128 BlockedWest;
    public UInt128 BlockedEast;

    /// <summary>Occupied horizontal wall slots, bit <c>r * 8 + c</c>.</summary>
    public ulong HorizontalWalls;

    public ulong VerticalWalls;

    /// <summary>
    /// Slots whose wall was placed by player 1, so the board can draw each wall in its
    /// owner's colour. Deliberately outside the Zobrist hash: who placed a wall changes
    /// nothing about how the position plays, and leaving it out lets two positions that
    /// differ only in that bookkeeping share a transposition entry.
    /// </summary>
    public ulong WallsByPlayer1;

    private byte _pawn0;
    private byte _pawn1;
    private byte _walls0;
    private byte _walls1;

    /// <summary>0 or 1 — whose turn it is.</summary>
    public byte SideToMove;

    /// <summary>
    /// Whether squares have been taken out of play. Constant for a whole game, and kept
    /// here only because the engine's fast wall-legality test is unsound on such a board:
    /// it decides from walls and borders alone, and a hole is neither. Sits in the struct's
    /// existing padding, so it costs the search nothing.
    /// </summary>
    public bool HasHoles;

    public ulong Hash;

    /// <summary>Half-moves played since the start of the game.</summary>
    public int Ply;

    public static GameState CreateInitial()
    {
        GameState s = default;

        // Edges act exactly like walls, so encode them once and never test bounds again.
        s.BlockedNorth = Board.TopRow;
        s.BlockedSouth = Board.BottomRow;
        s.BlockedWest = Board.LeftColumn;
        s.BlockedEast = Board.RightColumn;

        s._pawn0 = (byte)Board.StartCell[0];
        s._pawn1 = (byte)Board.StartCell[1];
        s._walls0 = Board.WallsPerPlayer;
        s._walls1 = Board.WallsPerPlayer;
        s.SideToMove = 0;
        s.Ply = 0;

        s.Hash = Zobrist.Pawn[0, s._pawn0] ^ Zobrist.Pawn[1, s._pawn1]
                 ^ Zobrist.WallsLeft[0, s._walls0] ^ Zobrist.WallsLeft[1, s._walls1];

        return s;
    }

    /// <summary>
    /// The starting position on a board with squares taken out of play.
    ///
    /// A hole is sealed on all four sides, and each of its neighbours is sealed against
    /// stepping into it — after which nothing else in the rules or the engine has to know
    /// about holes at all, because they are already walls as far as the block masks are
    /// concerned. The layout is not part of the hash: it never changes within a game, and
    /// a transposition table is never shared between games.
    /// </summary>
    public static GameState CreateInitial(BoardLayout layout)
    {
        GameState s = CreateInitial();

        UInt128 holes = Layouts.Holes(layout);
        if (holes == 0) return s;

        s.HasHoles = true;

        for (int cell = 0; cell < Board.CellCount; cell++)
        {
            if ((holes & Board.Bit(cell)) == 0) continue;

            int row = Board.RowOf(cell);
            int col = Board.ColOf(cell);
            UInt128 bit = Board.Bit(cell);

            // Nothing leaves the hole…
            s.BlockedNorth |= bit;
            s.BlockedSouth |= bit;
            s.BlockedWest |= bit;
            s.BlockedEast |= bit;

            // …and nothing steps into it.
            if (row > 0) s.BlockedSouth |= Board.Bit(Board.Index(row - 1, col));
            if (row < Board.Size - 1) s.BlockedNorth |= Board.Bit(Board.Index(row + 1, col));
            if (col > 0) s.BlockedEast |= Board.Bit(Board.Index(row, col - 1));
            if (col < Board.Size - 1) s.BlockedWest |= Board.Bit(Board.Index(row, col + 1));
        }

        return s;
    }

    /// <summary>
    /// Builds an arbitrary position with no walls on the board. Intended for tests,
    /// puzzles and analysis; the caller is responsible for the position making sense.
    /// </summary>
    public static GameState Create(int pawn0, int pawn1, int walls0, int walls1, int sideToMove)
    {
        GameState s = CreateInitial();

        s.Hash ^= Zobrist.Pawn[0, s._pawn0] ^ Zobrist.Pawn[1, s._pawn1]
                  ^ Zobrist.WallsLeft[0, s._walls0] ^ Zobrist.WallsLeft[1, s._walls1];

        s._pawn0 = (byte)pawn0;
        s._pawn1 = (byte)pawn1;
        s._walls0 = (byte)walls0;
        s._walls1 = (byte)walls1;
        s.SideToMove = (byte)sideToMove;

        s.Hash ^= Zobrist.Pawn[0, s._pawn0] ^ Zobrist.Pawn[1, s._pawn1]
                  ^ Zobrist.WallsLeft[0, s._walls0] ^ Zobrist.WallsLeft[1, s._walls1];
        if (sideToMove == 1) s.Hash ^= Zobrist.SideToMove;

        return s;
    }

    public readonly int PawnOf(int player) => player == 0 ? _pawn0 : _pawn1;

    public readonly int WallsOf(int player) => player == 0 ? _walls0 : _walls1;

    /// <summary>Which player placed the wall occupying a slot. Meaningless for empty slots.</summary>
    public readonly int WallOwner(int slot) => (WallsByPlayer1 & (1UL << slot)) != 0 ? 1 : 0;

    public readonly int Opponent => SideToMove ^ 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Blocked(int cell, int direction)
    {
        UInt128 bit = Board.Bit(cell);
        return direction switch
        {
            Board.North => (BlockedNorth & bit) != 0,
            Board.South => (BlockedSouth & bit) != 0,
            Board.West => (BlockedWest & bit) != 0,
            _ => (BlockedEast & bit) != 0,
        };
    }

    /// <summary>The player who has reached their goal row, or -1 if the game is live.</summary>
    public readonly int Winner
    {
        get
        {
            if (Board.RowOf(_pawn0) == Board.GoalRow(0)) return 0;
            if (Board.RowOf(_pawn1) == Board.GoalRow(1)) return 1;
            return -1;
        }
    }

    public readonly bool IsGameOver => Winner >= 0;

    // ---------------------------------------------------------------- walls --

    /// <summary>
    /// Geometric legality only: the slot is free, the wall does not overlap a
    /// parallel neighbour, and it does not cross a perpendicular wall.
    /// Does not check that both players still have a path.
    /// </summary>
    public readonly bool IsSlotFree(MoveKind kind, int row, int col)
    {
        if (!Board.SlotInBounds(row, col)) return false;

        ulong bit = 1UL << Board.SlotIndex(row, col);

        // A slot centre hosts at most one wall of either orientation.
        if (((HorizontalWalls | VerticalWalls) & bit) != 0) return false;

        if (kind == MoveKind.HorizontalWall)
        {
            // Overlaps the left/right neighbour, which spans this slot's first/second half.
            if (col > 0 && (HorizontalWalls & (bit >> 1)) != 0) return false;
            if (col < Board.SlotSize - 1 && (HorizontalWalls & (bit << 1)) != 0) return false;
        }
        else
        {
            if (row > 0 && (VerticalWalls & (bit >> Board.SlotSize)) != 0) return false;
            if (row < Board.SlotSize - 1 && (VerticalWalls & (bit << Board.SlotSize)) != 0) return false;
        }

        return true;
    }

    /// <summary>
    /// Full legality for the side to move: they have a wall in hand, the geometry
    /// works, and neither player ends up sealed off from their goal row.
    /// </summary>
    public readonly bool IsWallLegal(MoveKind kind, int row, int col)
    {
        if (kind == MoveKind.Pawn) return false;
        if (WallsOf(SideToMove) == 0) return false;
        if (!IsSlotFree(kind, row, col)) return false;

        GameState probe = this;
        probe.PlaceWallUnchecked(kind, row, col);
        return PathFinder.HasPath(probe, 0) && PathFinder.HasPath(probe, 1);
    }

    /// <summary>Applies a wall to the block masks. Caller guarantees legality.</summary>
    public void PlaceWallUnchecked(MoveKind kind, int row, int col)
    {
        ulong bit = 1UL << Board.SlotIndex(row, col);

        if (kind == MoveKind.HorizontalWall)
        {
            HorizontalWalls |= bit;

            UInt128 above = Board.Bit(Board.Index(row, col)) | Board.Bit(Board.Index(row, col + 1));
            UInt128 below = Board.Bit(Board.Index(row + 1, col)) | Board.Bit(Board.Index(row + 1, col + 1));

            BlockedSouth |= above;
            BlockedNorth |= below;
        }
        else
        {
            VerticalWalls |= bit;

            UInt128 left = Board.Bit(Board.Index(row, col)) | Board.Bit(Board.Index(row + 1, col));
            UInt128 right = Board.Bit(Board.Index(row, col + 1)) | Board.Bit(Board.Index(row + 1, col + 1));

            BlockedEast |= left;
            BlockedWest |= right;
        }
    }

    // ---------------------------------------------------------------- moves --

    /// <summary>
    /// Writes every legal pawn step for the side to move into <paramref name="dest"/>
    /// and returns the count. At most 5 moves exist, so a 8-wide stack buffer is plenty.
    /// </summary>
    public readonly int GeneratePawnMoves(Span<Move> dest)
    {
        int count = 0;
        int me = PawnOf(SideToMove);
        int opponent = PawnOf(Opponent);

        for (int dir = 0; dir < 4; dir++)
        {
            if (Blocked(me, dir)) continue;

            int target = me + Board.Delta[dir];

            if (target != opponent)
            {
                dest[count++] = Move.ToCell(target);
                continue;
            }

            // Facing the opponent: hop straight over when nothing is behind them,
            // otherwise step diagonally around either side.
            if (!Blocked(target, dir))
            {
                dest[count++] = Move.ToCell(target + Board.Delta[dir]);
                continue;
            }

            foreach (int side in Board.Perpendicular[dir])
            {
                if (!Blocked(target, side))
                    dest[count++] = Move.ToCell(target + Board.Delta[side]);
            }
        }

        return count;
    }

    public readonly bool IsPawnMoveLegal(int row, int col)
    {
        if (!Board.InBounds(row, col)) return false;

        Span<Move> buffer = stackalloc Move[8];
        int count = GeneratePawnMoves(buffer);
        int cell = Board.Index(row, col);

        for (int i = 0; i < count; i++)
            if (buffer[i].Cell == cell) return true;

        return false;
    }

    public readonly bool IsLegal(Move move) =>
        move.Kind == MoveKind.Pawn
            ? IsPawnMoveLegal(move.Row, move.Col)
            : IsWallLegal(move.Kind, move.Row, move.Col);

    /// <summary>Every legal move for the side to move, pawn steps first.</summary>
    public readonly List<Move> LegalMoves()
    {
        var moves = new List<Move>(64);

        Span<Move> pawn = stackalloc Move[8];
        int count = GeneratePawnMoves(pawn);
        for (int i = 0; i < count; i++) moves.Add(pawn[i]);

        AppendWallMoves(moves);
        return moves;
    }

    /// <summary>Appends every legal wall placement for the side to move.</summary>
    public readonly void AppendWallMoves(List<Move> moves)
    {
        if (WallsOf(SideToMove) == 0) return;

        for (int row = 0; row < Board.SlotSize; row++)
        {
            for (int col = 0; col < Board.SlotSize; col++)
            {
                if (IsWallLegal(MoveKind.HorizontalWall, row, col))
                    moves.Add(new Move(MoveKind.HorizontalWall, row, col));

                if (IsWallLegal(MoveKind.VerticalWall, row, col))
                    moves.Add(new Move(MoveKind.VerticalWall, row, col));
            }
        }
    }

    /// <summary>Applies a move that the caller has already validated.</summary>
    public void Apply(Move move)
    {
        int player = SideToMove;

        if (move.Kind == MoveKind.Pawn)
        {
            int from = PawnOf(player);
            int to = move.Cell;

            Hash ^= Zobrist.Pawn[player, from] ^ Zobrist.Pawn[player, to];

            if (player == 0) _pawn0 = (byte)to;
            else _pawn1 = (byte)to;
        }
        else
        {
            PlaceWallUnchecked(move.Kind, move.Row, move.Col);

            int slot = move.Slot;
            if (player == 1) WallsByPlayer1 |= 1UL << slot;
            int before = WallsOf(player);
            int after = before - 1;

            Hash ^= Zobrist.Wall[move.IsHorizontal ? 0 : 1, slot];
            Hash ^= Zobrist.WallsLeft[player, before] ^ Zobrist.WallsLeft[player, after];

            if (player == 0) _walls0 = (byte)after;
            else _walls1 = (byte)after;
        }

        SideToMove = (byte)(player ^ 1);
        Hash ^= Zobrist.SideToMove;
        Ply++;
    }
}
