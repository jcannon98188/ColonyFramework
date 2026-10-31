using System.Runtime.InteropServices;

namespace Colony;

/// <summary>
/// The World's keyed Resource store (spec sections 4.1 and 4.3): one native block holding every value
/// Resource at a 64-byte aligned offset, zero-initialised at creation, plus one native growable queue per
/// event Resource. Values live for the World; queues grow (native realloc, doubling) only when a merge
/// appends past their capacity, so after warm-up a Tick allocates nothing. Appends happen only at sync
/// points (command-buffer playback), never from chunk jobs.
/// </summary>
internal sealed unsafe class ResourceStore : IDisposable
{
    private const int SlotAlignment = 64;
    private const int InitialQueueCapacity = 64;

    private readonly int[] _offsets;
    private readonly int[] _sizes;
    private readonly byte*[] _queues;
    private readonly int[] _counts;
    private readonly int[] _capacities;
    private byte* _values;
    private bool _disposed;

    public ResourceStore(WorldSchema schema)
    {
        int n = schema.ResourceCount;
        _offsets = new int[n];
        _sizes = new int[n];
        _queues = new byte*[n];
        _counts = new int[n];
        _capacities = new int[n];
        int offset = 0;
        for (int id = 0; id < n; id++)
        {
            var descriptor = schema.Resources[id];
            _sizes[id] = descriptor.Size;
            if (descriptor.Kind != ResourceKind.Value)
            {
                _offsets[id] = -1;
                continue;
            }

            _offsets[id] = offset;
            offset += (descriptor.Size + SlotAlignment - 1) & ~(SlotAlignment - 1);
        }

        ValueBytes = offset;
        if (offset > 0)
        {
            _values = (byte*)NativeMemory.AlignedAlloc((nuint)offset, SlotAlignment);
            NativeMemory.Clear(_values, (nuint)offset);
        }
    }

    /// <summary>Bytes of the value block, for diagnostics.</summary>
    public int ValueBytes { get; }

    /// <summary>Address of a value Resource; stable for the World's lifetime.</summary>
    public byte* Pointer(int id) => _values + _offsets[id];

    /// <summary>Pending events in a queue.</summary>
    public int Count(int id) => _counts[id];

    /// <summary>Start of a queue's pending events; valid until the next append or clear.</summary>
    public byte* QueuePointer(int id) => _queues[id];

    /// <summary>Appends one event (<see cref="WorldSchema.ResourceSizes"/> bytes) to a queue.</summary>
    public void Append(int id, byte* source)
    {
        int size = _sizes[id];
        if (_counts[id] == _capacities[id])
        {
            int capacity = _capacities[id] == 0 ? InitialQueueCapacity : _capacities[id] * 2;
            _queues[id] = (byte*)NativeMemory.Realloc(_queues[id], (nuint)capacity * (nuint)size);
            _capacities[id] = capacity;
        }

        Buffer.MemoryCopy(source, _queues[id] + (long)_counts[id] * size, size, size);
        _counts[id]++;
    }

    public void Clear(int id) => _counts[id] = 0;

    public void ClearAllQueues() => Array.Clear(_counts);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NativeMemory.AlignedFree(_values);
        _values = null;
        for (int id = 0; id < _queues.Length; id++)
        {
            NativeMemory.Free(_queues[id]);
            _queues[id] = null;
        }
    }
}
