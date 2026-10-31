using System.Runtime.InteropServices;
using Colony.Hashing;

namespace Colony;

/// <summary>
/// The World's interned string table (spec section 4.1): <see cref="Intern(ReadOnlySpan{char})"/> maps text to a
/// dense <see cref="StringId"/> in insertion order, so identical insertion sequences give identical ids and the
/// table is saved World state (part of <see cref="World.Hash"/>, in id order). Strings are never removed.
/// Interning is serial (outside a Tick or from a serial hook); <see cref="Get"/> and <see cref="TryGetId"/> are
/// safe from chunk jobs while nothing interns. Looking up an existing string allocates nothing.
/// </summary>
public sealed class StringTable : IDisposable
{
    private const ulong LookupSeed = 0x5354_5249; // "STRI"
    private readonly NativeBytes _chars = new(1024);
    private long[] _offsets = new long[64];   // id - 1 -> byte offset into _chars
    private int[] _lengths = new int[64];     // id - 1 -> length in chars
    private ulong[] _hashes = new ulong[64];  // id - 1 -> content hash (avoids rehashing on table growth)
    private int[] _buckets = new int[128];    // open addressing: 0 = empty, otherwise id
    private int _count;

    internal StringTable()
    {
    }

    /// <summary>Interned strings so far; the next id is <c>Count + 1</c>.</summary>
    public int Count => _count;

    /// <summary>The id of <paramref name="text"/>, interning it when it is new.</summary>
    public StringId Intern(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Intern(text.AsSpan());
    }

    /// <summary>The id of <paramref name="text"/>, interning it when it is new.</summary>
    public StringId Intern(ReadOnlySpan<char> text)
    {
        ulong hash = Hash64.Compute(MemoryMarshal.AsBytes(text), LookupSeed);
        int bucket = Find(text, hash, out int id);
        if (id != 0)
        {
            return new StringId((uint)id);
        }

        if (_count == _offsets.Length)
        {
            Array.Resize(ref _offsets, _count * 2);
            Array.Resize(ref _lengths, _count * 2);
            Array.Resize(ref _hashes, _count * 2);
        }

        id = ++_count;
        _offsets[id - 1] = _chars.Append(MemoryMarshal.AsBytes(text), sizeof(char));
        _lengths[id - 1] = text.Length;
        _hashes[id - 1] = hash;
        if (_count * 2 > _buckets.Length)
        {
            Rehash(_buckets.Length * 2);
        }
        else
        {
            _buckets[bucket] = id;
        }

        return new StringId((uint)id);
    }

    /// <summary>The id of an already interned <paramref name="text"/>, without interning it.</summary>
    public bool TryGetId(ReadOnlySpan<char> text, out StringId id)
    {
        Find(text, Hash64.Compute(MemoryMarshal.AsBytes(text), LookupSeed), out int found);
        id = new StringId((uint)found);
        return found != 0;
    }

    /// <summary>The text behind <paramref name="id"/>; empty for <see cref="StringId.Null"/>, throws for an id this table never issued.</summary>
    public ReadOnlySpan<char> Get(StringId id)
    {
        if (id.IsNull)
        {
            return default;
        }

        if (id.Value > (uint)_count)
        {
            throw new ArgumentException($"{id} was not issued by this string table ({_count} strings).", nameof(id));
        }

        int index = (int)id.Value - 1;
        return MemoryMarshal.Cast<byte, char>(_chars.Slice(_offsets[index], _lengths[index] * sizeof(char)));
    }

    /// <summary>Appends every string in id order: the saved form of the table.</summary>
    internal void Hash(ref Hash64State state)
    {
        state.Append(_count);
        for (int i = 0; i < _count; i++)
        {
            state.Append(_lengths[i]);
            state.Append(_chars.Slice(_offsets[i], _lengths[i] * sizeof(char)));
        }
    }

    /// <summary>Probes for <paramref name="text"/>; returns the bucket where it is or where it would go.</summary>
    private int Find(ReadOnlySpan<char> text, ulong hash, out int id)
    {
        int mask = _buckets.Length - 1;
        int bucket = (int)(hash & (ulong)mask);
        while (true)
        {
            int candidate = _buckets[bucket];
            if (candidate == 0)
            {
                id = 0;
                return bucket;
            }

            int index = candidate - 1;
            if (_hashes[index] == hash && _lengths[index] == text.Length
                && MemoryMarshal.AsBytes(text).SequenceEqual(_chars.Slice(_offsets[index], _lengths[index] * sizeof(char))))
            {
                id = candidate;
                return bucket;
            }

            bucket = (bucket + 1) & mask;
        }
    }

    private void Rehash(int size)
    {
        _buckets = new int[size];
        int mask = size - 1;
        for (int id = 1; id <= _count; id++)
        {
            int bucket = (int)(_hashes[id - 1] & (ulong)mask);
            while (_buckets[bucket] != 0)
            {
                bucket = (bucket + 1) & mask;
            }

            _buckets[bucket] = id;
        }
    }

    public void Dispose() => _chars.Dispose();
}
