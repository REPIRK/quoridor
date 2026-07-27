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

    /// <summary>Squares still holding a spare wall to pick up. Cleared as they are taken.</summary>
    public UInt128 WallPickups;

    /// <summary>Squares still holding a free move — take one and the opponent is skipped.</summary>
    public UInt128 SkipPickups;

    private byte _pawn0;
    private byte _pawn1;
    private byte _walls0;
    private byte _walls1;

    /// <summary>
    /// The rows the two players are trying to reach. Normally 0 and 8, but a smaller
    /// game is played on a centred square of the same grid, and then they move inward.
    /// They also give the playable area away: it is the square between them.
    /// </summary>
    private byte _goal0;
    private byte _goal1;

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

        s._goal0 = 0;
        s._goal1 = Board.Size - 1;

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
    /// The starting position of a game played on a centred square of the grid, with a
    /// wall supply of its own. A 9-wide game is the ordinary board; anything smaller
    /// leaves a ring around it that is simply not part of the game, sealed exactly the
    /// way a hole is.
    /// </summary>
    public static GameState CreateInitial(int size, int walls)
    {
        if ((size & 1) == 0 || size < 3 || size > Board.Size)
            throw new ArgumentOutOfRangeException(nameof(size), size, "Board size must be odd and fit the grid.");

        GameState s = CreateInitial();

        walls = Math.Clamp(walls, 0, Board.MaxWalls);
        s.Hash ^= Zobrist.WallsLeft[0, s._walls0] ^ Zobrist.WallsLeft[1, s._walls1];
        s._walls0 = (byte)walls;
        s._walls1 = (byte)walls;
        s.Hash ^= Zobrist.WallsLeft[0, s._walls0] ^ Zobrist.WallsLeft[1, s._walls1];

        if (size == Board.Size) return s;

        int origin = (Board.Size - size) / 2;

        s._goal0 = (byte)origin;
        s._goal1 = (byte)(origin + size - 1);

        s.Hash ^= Zobrist.Pawn[0, s._pawn0] ^ Zobrist.Pawn[1, s._pawn1];
        s._pawn0 = (byte)Board.Index(s._goal1, origin + size / 2);
        s._pawn1 = (byte)Board.Index(s._goal0, origin + size / 2);
        s.Hash ^= Zobrist.Pawn[0, s._pawn0] ^ Zobrist.Pawn[1, s._pawn1];

        // The ring outside the game is taken out of play like any other hole.
        for (int cell = 0; cell < Board.CellCount; cell++)
        {
            int row = Board.RowOf(cell);
            int col = Board.ColOf(cell);

            if (row >= origin && row <= s._goal1 && col >= origin && col <= s._goal1) continue;

            s.SealCell(cell);
        }

        return s;
    }

    /// <summary>
    /// Takes a square out of play: nothing leaves it and nothing steps into it. After
    /// this the rules, the flood fill and the search need no idea holes exist, because
    /// as far as the block masks are concerned it is already walled off on all sides.
    /// </summary>
    public void SealCell(int cell)
    {
        int row = Board.RowOf(cell);
        int col = Board.ColOf(cell);
        UInt128 bit = Board.Bit(cell);

        HasHoles = true;

        BlockedNorth |= bit;
        BlockedSouth |= bit;
        BlockedWest |= bit;
        BlockedEast |= bit;

        if (row > 0) BlockedSouth |= Board.Bit(Board.Index(row - 1, col));
        if (row < Board.Size - 1) BlockedNorth |= Board.Bit(Board.Index(row + 1, col));
        if (col > 0) BlockedEast |= Board.Bit(Board.Index(row, col - 1));
        if (col < Board.Size - 1) BlockedWest |= Board.Bit(Board.Index(row, col + 1));
    }

    /// <summary>Puts a pickup on a square. Both kinds are part of the hash.</summary>
    public void PlacePickup(int cell, PickupKind kind)
    {
        if (kind == PickupKind.Wall)
        {
            WallPickups |= Board.Bit(cell);
            Hash ^= Zobrist.Pickup[0, cell];
        }
        else
        {
            SkipPickups |= Board.Bit(cell);
            Hash ^= Zobrist.Pickup[1, cell];
        }
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

    /// <summary>The row the given player is trying to reach.</summary>
    public readonly int GoalRow(int player) => player == 0 ? _goal0 : _goal1;

    /// <summary>That row as a bitboard, which is what the flood fill starts from.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly UInt128 GoalMask(int player) => Board.RowMask[player == 0 ? _goal0 : _goal1];

    /// <summary>Whether any pickup is still lying on the board.</summary>
    public readonly bool HasPickups => (WallPickups | SkipPickups) != 0;

    /// <summary>The player who has reached their goal row, or -1 if the game is live.</summary>
    public readonly int Winner
    {
        get
        {
            if (Board.RowOf(_pawn0) == _goal0) return 0;
            if (Board.RowOf(_pawn1) == _goal1) return 1;
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

        // A slot exists only where all four squares around it are in the game, which on
        // a smaller board rules out the whole ring the game is not played on.
        if (row < _goal0 || row >= _goal1 || col < _goal0 || col >= _goal1) return false;

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

        // Set by a free move picked up on the square just stepped onto: the turn does
        // not pass, so the same player moves again.
        bool again = false;

        if (move.Kind == MoveKind.Pawn)
        {
            int from = PawnOf(player);
            int to = move.Cell;

            Hash ^= Zobrist.Pawn[player, from] ^ Zobrist.Pawn[player, to];

            if (player == 0) _pawn0 = (byte)to;
            else _pawn1 = (byte)to;

            if (HasPickups) again = Collect(to, player);
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

        if (!again)
        {
            SideToMove = (byte)(player ^ 1);
            Hash ^= Zobrist.SideToMove;
        }

        Ply++;
    }

    /// <summary>
    /// Takes whatever was lying on the square just stepped onto. Returns true when it
    /// was a free move, which is the one effect the caller has to act on.
    /// </summary>
    private bool Collect(int cell, int player)
    {
        UInt128 bit = Board.Bit(cell);

        if ((WallPickups & bit) != 0)
        {
            WallPickups &= ~bit;
            Hash ^= Zobrist.Pickup[0, cell];

            int before = WallsOf(player);
            if (before < Board.MaxWalls)
            {
                Hash ^= Zobrist.WallsLeft[player, before] ^ Zobrist.WallsLeft[player, before + 1];

                if (player == 0) _walls0 = (byte)(before + 1);
                else _walls1 = (byte)(before + 1);
            }

            return false;
        }

        if ((SkipPickups & bit) == 0) return false;

        SkipPickups &= ~bit;
        Hash ^= Zobrist.Pickup[1, cell];

        // Reaching your goal row ends the game; another move on top of it would be a
        // move with nowhere to go.
        return !IsGameOver;
    }
}
