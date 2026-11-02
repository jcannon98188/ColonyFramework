using System.Runtime.InteropServices;

namespace Colony;

/// <summary>
/// Storage for Components flagged <see cref="ComponentOptions.NonFragmenting"/>: one native column per
/// Component indexed by Entity slot plus a presence bitset, sized to the Entity index. Adding or
/// removing such a Component flips a bit instead of moving the Entity between archetypes.
/// </summary>
internal sealed unsafe class NonFragmentingStore : IDisposable
{
    private readonly int[] _sizes;
    private readonly byte*[] _columns;
    private readonly ulong*[] _presence;
    private readonly ulong*[] _enabled;
    private int _capacity;
    private bool _disposed;

    public NonFragmentingStore(WorldSchema schema, int initialCapacity)
    {
        _sizes = new int[schema.NonFragmentingCount];
        for (int id = 0; id < schema.ComponentCount; id++)
        {
            int slot = schema.NonFragmentingSlots[id];
            if (slot >= 0)
            {
                _sizes[slot] = schema.Sizes[id];
            }
        }

        _columns = new byte*[_sizes.Length];
        _presence = new ulong*[_sizes.Length];
        _enabled = new ulong*[_sizes.Length];
        EnsureCapacity(initialCapacity);
    }

    public int Capacity => _capacity;

    public void EnsureCapacity(int entityCapacity)
    {
        if (entityCapacity <= _capacity)
        {
            return;
        }

        int capacity = Math.Max(64, _capacity);
        while (capacity < entityCapacity)
        {
            capacity *= 2;
        }

        for (int slot = 0; slot < _sizes.Length; slot++)
        {
            nuint oldBytes = (nuint)_capacity * (nuint)_sizes[slot];
            nuint newBytes = (nuint)capacity * (nuint)_sizes[slot];
            _columns[slot] = (byte*)NativeMemory.Realloc(_columns[slot], newBytes);
            NativeMemory.Clear(_columns[slot] + oldBytes, newBytes - oldBytes);

            nuint oldWords = (nuint)((_capacity + 63) / 64) * sizeof(ulong);
            nuint newWords = (nuint)((capacity + 63) / 64) * sizeof(ulong);
            _presence[slot] = (ulong*)NativeMemory.Realloc(_presence[slot], newWords);
            NativeMemory.Clear((byte*)_presence[slot] + oldWords, newWords - oldWords);
            _enabled[slot] = (ulong*)NativeMemory.Realloc(_enabled[slot], newWords);
            NativeMemory.Clear((byte*)_enabled[slot] + oldWords, newWords - oldWords);
        }

        _capacity = capacity;
    }

    public bool Has(int slot, int index) => (_presence[slot][index >> 6] & (1UL << (index & 63))) != 0;

    public byte* Pointer(int slot, int index) => _columns[slot] + (long)index * _sizes[slot];

    public bool IsEnabled(int slot, int index) => (_enabled[slot][index >> 6] & (1UL << (index & 63))) != 0;

    public void SetEnabled(int slot, int index, bool enabled)
    {
        ulong bit = 1UL << (index & 63);
        if (enabled)
        {
            _enabled[slot][index >> 6] |= bit;
        }
        else
        {
            _enabled[slot][index >> 6] &= ~bit;
        }
    }

    /// <summary>Marks the Component present and enabled and copies <paramref name="source"/> (or zeroes the value when null).</summary>
    public void Set(int slot, int index, byte* source)
    {
        _presence[slot][index >> 6] |= 1UL << (index & 63);
        _enabled[slot][index >> 6] |= 1UL << (index & 63);
        byte* destination = Pointer(slot, index);
        if (source is null)
        {
            NativeMemory.Clear(destination, (nuint)_sizes[slot]);
        }
        else
        {
            Buffer.MemoryCopy(source, destination, _sizes[slot], _sizes[slot]);
        }
    }

    public void Remove(int slot, int index)
    {
        _presence[slot][index >> 6] &= ~(1UL << (index & 63));
        _enabled[slot][index >> 6] &= ~(1UL << (index & 63));
    }

    /// <summary>Drops every non-fragmenting Component of a destroyed Entity.</summary>
    public void ClearEntity(int index)
    {
        for (int slot = 0; slot < _sizes.Length; slot++)
        {
            Remove(slot, index);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (int slot = 0; slot < _sizes.Length; slot++)
        {
            NativeMemory.Free(_columns[slot]);
            NativeMemory.Free(_presence[slot]);
            NativeMemory.Free(_enabled[slot]);
            _columns[slot] = null;
            _presence[slot] = null;
            _enabled[slot] = null;
        }
    }
}
