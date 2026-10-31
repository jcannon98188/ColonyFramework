using System.Runtime.InteropServices;

namespace Colony;

/// <summary>
/// The World's keyed Resource store (spec section 4.1): one native block holding every value Resource at
/// a 64-byte aligned offset, zero-initialised at creation. Values live for the World; nothing here
/// allocates after construction.
/// </summary>
internal sealed unsafe class ResourceStore : IDisposable
{
    private const int SlotAlignment = 64;

    private readonly int[] _offsets;
    private byte* _values;
    private bool _disposed;

    public ResourceStore(WorldSchema schema)
    {
        _offsets = new int[schema.ResourceCount];
        int offset = 0;
        for (int id = 0; id < schema.ResourceCount; id++)
        {
            var descriptor = schema.Resources[id];
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NativeMemory.AlignedFree(_values);
        _values = null;
    }
}
