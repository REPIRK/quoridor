using System.Runtime.CompilerServices;

namespace Quoridor.Core;

/// <summary>
/// A complete Quoridor position. Deliberately a mutable struct: the search copies it
/// (<c>var next = state; next.Apply(move);</c>) instead of implementing undo, which
/// removes a whole class of bugs — no unmake to get wrong, and no path by which a
/// mis-ordered undo can leave a corrupted position behind for the rest of the search.
///
/// The copy is not free and this comment used to claim it was. Measured,
/// <c>Unsafe.SizeOf&lt;GameState&gt;()</c> is 160 bytes: two and a half 64-byte cache
/// lines, not under two. What makes it worth paying is that 160 bytes is ten branch-free
/// vector moves the compiler emits inline, against an unmake path that would have to
/// undo a wall, two blocked-direction bitboards, a pickup, a hash and a turn in the right
/// order every time. With one copy at every node, a single-threaded fixed-depth search
/// over the five bench boards still measured about 1,030 ns per node — and the whole of
/// that node is a move generation, two flood fills per candidate wall and an evaluation,
/// with the copy a small part of it.
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

    /// <summary>
    /// Portal pairs, one bit per pair, indexed by the pair's lower cell. A portal links
    /// cell <c>c</c> to <c>Board.CellCount - 1 - c</c> — the half turn that maps one
    /// player's half onto the other's, so a portal board is fair by its geometry rather
    /// than by the placement having been careful. The centre cell is its own mirror and
    /// is never set.
    ///
    /// Deliberately outside the Zobrist hash, for the same reason a hole is: portals are
    /// permanent, so two positions in one game always share them and they separate
    /// nothing. Making a portal one-shot or cooling-down would give it state, and then it
    /// needs its own Zobrist table — the same edit that would break repetition detection,
    /// because the two are one fact.
    ///
    /// One <c>ulong</c> rather than a mouth bitboard on purpose: it lands in the eight
    /// bytes of padding the 16-byte-aligned pickup boards leave behind, so the struct the
    /// search copies at every node does not grow. A <c>UInt128</c> here would widen every
    /// node of every search, including on boards that have no portals at all.
    /// </summary>
    public ulong Portals;

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

    /// <summary>How many walls a spare-wall pickup is worth.</summary>
    public const int WallsPerPickup = 2;

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

    /// <summary>
    /// Whether the board has portals at all. Every portal-aware path is guarded by this,
    /// so a board without them runs the code it ran before portals existed.
    /// </summary>
    public readonly bool HasPortals => Portals != 0;

    /// <summary>The square a portal at this one leads to. Meaningless off a portal.</summary>
    public static int PortalPartner(int cell) => Board.CellCount - 1 - cell;

    /// <summary>Whether a portal has one of its two mouths on this square.</summary>
    public readonly bool IsPortalMouth(int cell)
    {
        // Either mouth names the pair, so normalise to the lower of the two and the
        // caller never has to know which end it is standing on.
        int partner = Board.CellCount - 1 - cell;
        return (Portals & (1UL << (cell < partner ? cell : partner))) != 0;
    }

    /// <summary>Both mouths of every portal, as a cell bitboard. Built once per fill.</summary>
    public readonly UInt128 PortalMouths()
    {
        UInt128 mouths = 0;
        ulong pairs = Portals;

        while (pairs != 0)
        {
            int low = System.Numerics.BitOperations.TrailingZeroCount(pairs);
            pairs &= pairs - 1;
            mouths |= Board.Bit(low) | Board.Bit(Board.CellCount - 1 - low);
        }

        return mouths;
    }

    /// <summary>Links a square to its opposite number under a half turn. Build time only.</summary>
    public void PlacePortal(int cell) =>
        Portals |= 1UL << Math.Min(cell, Board.CellCount - 1 - cell);

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
    /// and returns the count. At most 8 moves exist — 5 without portals, and 4 plain
    /// steps plus the far mouth's 4 free sides when a portal's other end is occupied —
    /// so a 10-wide stack buffer is plenty.
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

        // A portal is an ordinary undirected edge, so travelling it is one step and it
        // passes the turn like any other. The Portals test comes first so a board without
        // them pays a single predicted-not-taken branch per generation.
        if (Portals != 0 && IsPortalMouth(me))
        {
            int far = PortalPartner(me);

            if (far != opponent)
            {
                dest[count++] = Move.ToCell(far);
            }
            else
            {
                // The portal edge has no axis, so "hop straight over" has no meaning —
                // which is the case the side-step branch above already exists for. All
                // four sides count, since none of them is back the way you came. No free
                // side and the portal is simply not on offer this turn, exactly as a jump
                // with nowhere to land is not.
                for (int side = 0; side < 4; side++)
                    if (!Blocked(far, side))
                        dest[count++] = Move.ToCell(far + Board.Delta[side]);
            }
        }

        return count;
    }

    public readonly bool IsPawnMoveLegal(int row, int col)
    {
        if (!Board.InBounds(row, col)) return false;

        Span<Move> buffer = stackalloc Move[10];
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

    /// <summary>
    /// Whether the side to move has anything at all to play. The pawn steps are asked for
    /// first because they answer yes in every position but a handful, and a wall search is
    /// eighty-odd slots of <see cref="IsWallLegal"/> — two flood fills each. So the
    /// expensive half only ever runs in the position it exists for, one where the pawn is
    /// boxed in, and that position is the one <see cref="Apply"/> turns into a forfeited
    /// turn.
    /// </summary>
    public readonly bool HasLegalMove()
    {
        Span<Move> buffer = stackalloc Move[10];
        if (GeneratePawnMoves(buffer) > 0) return true;

        // A wall in hand is not a move: the board can be full, and on a boxed-in position
        // it very nearly is. Ask.
        if (WallsOf(SideToMove) == 0) return false;

        for (int row = 0; row < Board.SlotSize; row++)
        {
            for (int col = 0; col < Board.SlotSize; col++)
            {
                if (IsWallLegal(MoveKind.HorizontalWall, row, col)) return true;
                if (IsWallLegal(MoveKind.VerticalWall, row, col)) return true;
            }
        }

        return false;
    }

    /// <summary>Every legal move for the side to move, pawn steps first.</summary>
    public readonly List<Move> LegalMoves()
    {
        var moves = new List<Move>(64);

        Span<Move> pawn = stackalloc Move[10];
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

        ForfeitTurnIfNothingToPlay();
    }

    /// <summary>
    /// A player who has no legal move forfeits the turn, and it comes straight back to
    /// the player who just moved.
    ///
    /// ---- why the rules need this at all ----
    ///
    /// Before portals, "the side to move always has a move" was a theorem rather than a
    /// rule, and <see cref="IsWallLegal"/> was its proof. If a mover had no pawn move then
    /// every direction out of their square was walled or led to the opponent with the jump
    /// and both side steps closed, so the flood fill's component was exactly the two pawn
    /// squares — and two adjacent squares cannot hold both goal rows, which are at least
    /// two rows apart on every board size. One of the two <c>HasPath</c> calls therefore
    /// failed and the wall was refused. A portal is a fifth edge out of a square, and it
    /// breaks that proof in two independent places. Both were reproduced before this was
    /// written, on positions built only from moves <c>IsLegal</c> accepted at the time:
    ///
    ///   pawns b8/b7 with a portal mouth under the opponent on b7, then H c7, V a8, V b8,
    ///   H b9 — the mover's own route leaves the pocket through the opponent's mouth, a
    ///   square no pawn may ever stand on. Erase the portal and both routes are gone.
    ///
    ///   pawns b8/b9 with a portal mouth under the opponent on b9, which is the mover's
    ///   goal row, then V a9, V b9, H b8 — here the mover's route is honest by the
    ///   ordinary rule that pawns are transparent, because the goal row is one step away
    ///   and merely occupied. It is the *opponent* who would have been sealed in, and
    ///   their own portal is what saves them. Erase the portal and HasPath(1) is false.
    ///
    /// In both, both HasPath calls return true, the wall is accepted, and once the mover's
    /// wall supply drains <c>LegalMoves()</c> is empty and every agent throws.
    ///
    /// ---- why not make the liveness test in IsWallLegal a real one ----
    ///
    /// Because the second family says it cannot be done there. "A route through a square
    /// the mover cannot enter is not a route" repairs the first family — suppressing a
    /// portal mouth the opponent is standing on costs one AND per fill, not per step, so
    /// the price would have been acceptable — and does nothing whatever for the second,
    /// where the mover's route is the one the standard rule already blesses. Repairing
    /// that one means making pawns opaque to the fill, which is a different game: the
    /// no-sealing rule ignores pawns precisely because pawns move, and an opaque fill
    /// would also hand the evaluation distances that are wrong by however long the
    /// opponent stands in a doorway.
    ///
    /// And a liveness test on walls is not closed even against family one, because a
    /// wall is not the only way in. A pawn that arrives on a square by portal can be the
    /// body that seals the opponent's last exit, so the test would have to cover pawn
    /// moves too — and forbidding a pawn move can remove the mover's own last move, at
    /// which point the rule is asking a player with one legal move not to play it.
    ///
    /// ---- what this costs instead ----
    ///
    /// Nothing on the flood fill, which is untouched: wall legality still runs the same
    /// two fills for each of the ~128 candidates at every node. The cost is one
    /// <see cref="GeneratePawnMoves"/> per <c>Apply</c>, against a node that already pays
    /// two 81-cell distance fills in the candidate generator. Measured over five fixed
    /// boards at depth 7, 178,808 nodes: 1,056 and 1,060 ns/node over two runs before,
    /// 1,057 and 1,061 after — inside the run-to-run spread, and the node counts are
    /// identical to the last node, because no forfeit happens in any of them.
    ///
    /// ---- and why it is invisible above Core ----
    ///
    /// A forfeit is deliberately not a Move. There is no <c>MoveKind.Pass</c>, so nothing
    /// changes in <see cref="Notation"/>, on the network wire, in the move list either
    /// front end renders, or in the search's negation — <c>SearchEngine.Child</c> already
    /// declines to negate a child whose side to move did not change, because a free move
    /// picked up off the board does exactly that, and a forfeited turn is that same shape
    /// seen from the other side. Both sessions already compute <c>LastMoveWentAgain</c>
    /// from <c>SideToMove == mover</c> for the same reason. Zobrist needs nothing new: the
    /// turn is the only thing that moves, and it is already hashed.
    ///
    /// It cannot loop, because two forfeits in a row are unreachable — and this is the
    /// part of the rule most worth reading before changing anything, because it is the
    /// only thing standing between a forfeit and a deadlock. A pawn with no step has every
    /// one of its four neighbours blocked or occupied by the opponent, and if it stands on
    /// a mouth then the far mouth must be the opponent too, or the portal step is a move.
    /// So there are exactly three ways both pawns can be stuck at once, and all three are
    /// already impossible:
    ///
    ///   * Each alone in its own component. Then <c>HasPath</c> needs that one square to
    ///     be the player's goal row, which is a won game, so the wall or hole that closed
    ///     the second one would have been refused.
    ///
    ///   * The two adjacent, the component being just the two squares. Neither can be a
    ///     mouth, since a square and its half-turn image are never neighbours on a grid of
    ///     odd cell count. Two adjacent squares cannot hold both goal rows, which are four
    ///     apart on the smallest board this plays, so again one <c>HasPath</c> fails.
    ///
    ///   * The two standing on the two mouths of one portal, not adjacent, the portal
    ///     being the whole of the component. This is the one the other two arguments do
    ///     not cover, and the only thing that rules it out is where mouths are allowed to
    ///     be. The two mouths are a half-turn pair, so if either is on a goal row the other
    ///     is on the opposite goal row, both <c>HasPath</c> calls pass, and the position is
    ///     legal with nobody able to move. <c>GameSetup.PlacePortals</c> keeps every mouth
    ///     off the goal rows and the rows beside them, which is what makes it unreachable
    ///     — a rule written for the balance of the game and now load-bearing for its
    ///     termination. The selftest asserts it ("avoids the goal rows and the rows beside
    ///     them"); anything that relaxes it, or any caller that reaches
    ///     <see cref="PlacePortal"/> directly with a goal-row square, brings this shape
    ///     back. Verified by construction over 480 generated boards and every adjacent,
    ///     portal-paired and isolated placement on each — no survivors — and by building
    ///     the goal-row mouth by hand, where both pawns really do end up with nothing.
    ///
    /// So the player who receives a forfeited turn always has something to play, and
    /// <see cref="Ply"/> keeps advancing. A game can still shuffle forever, exactly as it
    /// could before, and repetition is scored against the shuffler by the search.
    /// </summary>
    private void ForfeitTurnIfNothingToPlay()
    {
        // A finished game is never handed on: reaching the goal row ends it, and the
        // search reads "the side to move is the loser" out of that.
        if (IsGameOver || HasLegalMove()) return;

        SideToMove = (byte)(SideToMove ^ 1);
        Hash ^= Zobrist.SideToMove;
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

            // Two, not one. Reaching a pickup that is not already on your route costs a
            // move to step aside and a move to come back; a single wall does not repay
            // that, so the prize was one nobody sensibly took.
            int before = WallsOf(player);
            int after = Math.Min(before + WallsPerPickup, Board.MaxWalls);

            if (after != before)
            {
                Hash ^= Zobrist.WallsLeft[player, before] ^ Zobrist.WallsLeft[player, after];

                if (player == 0) _walls0 = (byte)after;
                else _walls1 = (byte)after;
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
