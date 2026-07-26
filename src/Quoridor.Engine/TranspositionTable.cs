using System.Runtime.CompilerServices;
using Quoridor.Core;

namespace Quoridor.Engine;

public enum Bound : byte
{
    Exact = 0,
    Lower = 1,
    Upper = 2,
}

public readonly struct TableEntry
{
    public readonly int Score;
    public readonly short Depth;
    public readonly Bound Bound;
    public readonly Move Move;
    public readonly bool HasMove;

    public TableEntry(int score, int depth, Bound bound, Move move, bool hasMove)
    {
        Score = score;
        Depth = (short)depth;
        Bound = bound;
        Move = move;
        HasMove = hasMove;
    }
}

/// <summary>
/// Shared transposition table.
///
/// Entries are two 64-bit words and are written without a lock: the stored key is
/// <c>hash ^ data</c>, so a torn write between the two words fails verification on
/// read and is simply treated as a miss. That is what makes the table safe to share
/// across the search threads, which is the whole point of having one — lazy SMP is
/// mostly threads feeding each other results through this table.
///
/// Callers must still validate any move they take from here: a surviving race, or a
/// hash collision, can hand back a move that is illegal in the current position.
/// </summary>
public sealed class TranspositionTable
{
    private struct Slot
    {
        public ulong Key;
        public ulong Data;
    }

    /// <summary>
    /// Entries per bucket. Two 16-byte slots share a cache line, so the second one is
    /// free to probe — and it lets the table keep a deep result and a recent one for
    /// the same index instead of making them fight over a single slot.
    ///
    /// The two slots are not interchangeable. Slot 0 is depth-preferred: a deep result
    /// stays there until something at least as deep, or a newer search, displaces it.
    /// Slot 1 is always-replace and absorbs everything else. Ranking both slots by
    /// depth and evicting the smaller — the obvious scheme — lets a flood of shallow
    /// nodes wash the deep results out, which is the whole thing this avoids.
    ///
    /// Measured against that obvious scheme it changed nothing at the time controls
    /// here, and that is the expected result: at 64 MB and roughly a million nodes per
    /// move the table never gets full enough for the policy to bite. It earns its keep
    /// on a smaller table or a longer clock.
    /// </summary>
    private const int Ways = 2;

    private const int DepthPreferred = 0;
    private const int AlwaysReplace = 1;

    private readonly Slot[] _slots;
    private readonly ulong _bucketMask;
    private byte _generation;

    public TranspositionTable(int megabytes = 64)
    {
        int bytesPerSlot = Unsafe.SizeOf<Slot>();
        long wantedSlots = Math.Max((long)Ways, Math.Max(1L, megabytes) * 1024L * 1024L / bytesPerSlot);
        long wantedBuckets = wantedSlots / Ways;

        int bits = 1;
        while (1L << (bits + 1) <= wantedBuckets && bits < 28) bits++;

        _slots = new Slot[(1 << bits) * Ways];
        _bucketMask = (ulong)(1 << bits) - 1;
    }

    public int SlotCount => _slots.Length;

    public void Clear() => Array.Clear(_slots);

    /// <summary>
    /// Marks the start of a new search. Entries from previous moves stay readable —
    /// they are still valid analysis — but they lose their claim on a slot, so the
    /// table does not silently fill up with deep entries from positions the game has
    /// long since left behind.
    /// </summary>
    public void NewGeneration() => _generation++;

    public bool TryGet(ulong hash, out TableEntry entry)
    {
        int index = (int)(hash & _bucketMask) * Ways;

        for (int way = 0; way < Ways; way++)
        {
            ref Slot slot = ref _slots[index + way];

            ulong key = Volatile.Read(ref slot.Key);
            ulong data = Volatile.Read(ref slot.Data);

            if ((key ^ data) == hash)
            {
                entry = Unpack(data);
                return true;
            }
        }

        entry = default;
        return false;
    }

    public void Store(ulong hash, int score, int depth, Bound bound, Move move, bool hasMove)
    {
        int index = (int)(hash & _bucketMask) * Ways;
        int target = -1;

        // This position already in the bucket? Then it is the one to update.
        for (int way = 0; way < Ways; way++)
        {
            ref Slot slot = ref _slots[index + way];

            ulong data = Volatile.Read(ref slot.Data);
            if ((Volatile.Read(ref slot.Key) ^ data) != hash) continue;

            int existingDepth = (byte)(data >> 32);
            if (existingDepth > depth && bound != Bound.Exact) return;

            target = index + way;
            break;
        }

        if (target < 0)
        {
            ulong guarded = Volatile.Read(ref _slots[index + DepthPreferred].Data);

            bool free = guarded == 0;
            bool stale = !free && (byte)(guarded >> 53) != _generation;
            bool shallower = !free && (byte)(guarded >> 32) <= depth;

            // Take the guarded slot only when it is not defending anything worth more
            // than this result; otherwise fall through to the one that always yields.
            target = index + (free || stale || shallower ? DepthPreferred : AlwaysReplace);
        }

        ulong packed = Pack(score, depth, bound, move, hasMove, _generation);

        Volatile.Write(ref _slots[target].Data, packed);
        Volatile.Write(ref _slots[target].Key, hash ^ packed);
    }

    // Layout: score 0..31, depth 32..39, bound 40..41, has-move 42, kind 43..44,
    // row 45..48, col 49..52, generation 53..60.
    private static ulong Pack(int score, int depth, Bound bound, Move move, bool hasMove, byte generation)
    {
        ulong data = (uint)score;
        data |= (ulong)(byte)Math.Clamp(depth, 0, 255) << 32;
        data |= (ulong)(byte)bound << 40;

        if (hasMove)
        {
            data |= 1UL << 42;
            data |= (ulong)(byte)move.Kind << 43;
            data |= (ulong)move.Row << 45;
            data |= (ulong)move.Col << 49;
        }

        data |= (ulong)generation << 53;
        return data;
    }

    private static TableEntry Unpack(ulong data)
    {
        int score = unchecked((int)(uint)data);
        int depth = (byte)(data >> 32);
        var bound = (Bound)(byte)((data >> 40) & 0x3);

        bool hasMove = (data & (1UL << 42)) != 0;
        Move move = default;

        if (hasMove)
        {
            var kind = (MoveKind)(byte)((data >> 43) & 0x3);
            int row = (int)((data >> 45) & 0xF);
            int col = (int)((data >> 49) & 0xF);
            move = new Move(kind, row, col);
        }

        return new TableEntry(score, depth, bound, move, hasMove);
    }
}
