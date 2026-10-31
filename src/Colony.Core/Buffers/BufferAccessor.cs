using System.Runtime.CompilerServices;

namespace Colony;

/// <summary>
/// Typed access to one Entity's buffer Component (<see cref="BufferComponent{TElement, TInline}"/>): a
/// growable list whose elements live inline in the chunk up to the declared capacity and in a World-owned
/// overflow block beyond it. Valid until the next structural change, like a column span. Growing from a chunk
/// job is allowed (the heap is locked); only ever touch buffers of rows in your own chunk, in your declared
/// Writes. <see cref="Clear"/> keeps the overflow block; it is released when the Entity or Component goes.
/// </summary>
public readonly unsafe ref struct BufferAccessor<T> where T : unmanaged
{
    private readonly BufferHeader* _header;
    private readonly byte* _inline;
    private readonly int _inlineCapacity;
    private readonly BufferHeap _heap;

    internal BufferAccessor(byte* component, int inlineOffset, int inlineCapacity, BufferHeap heap)
    {
        _header = (BufferHeader*)component;
        _inline = component + inlineOffset;
        _inlineCapacity = inlineCapacity;
        _heap = heap;
    }

    /// <summary>Most elements any buffer of <typeparamref name="T"/> can hold: one 64 KiB block.</summary>
    public int MaxLength => BufferHeap.MaxBlockSize / Unsafe.SizeOf<T>();

    public int Length => _header->Length;

    /// <summary>Elements that fit without another allocation: the inline capacity, or the current block's.</summary>
    public int Capacity => IsInline ? _inlineCapacity : BufferHeap.CapacityOf(_header->Handle, Unsafe.SizeOf<T>());

    /// <summary>Elements the Component holds before overflowing.</summary>
    public int InlineCapacity => _inlineCapacity;

    public bool IsInline => _header->Handle == 0;

    public bool IsEmpty => _header->Length == 0;

    private T* Data => (T*)(IsInline ? _inline : _heap.Pointer(_header->Handle));

    /// <summary>The live elements.</summary>
    public Span<T> AsSpan() => new(Data, _header->Length);

    public ref T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_header->Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return ref Data[index];
        }
    }

    public void Add(in T value)
    {
        int length = _header->Length;
        if (length == Capacity)
        {
            Grow(length + 1);
        }

        Data[length] = value;
        _header->Length = length + 1;
    }

    /// <summary>Removes the element at <paramref name="index"/>, shifting later elements down (order preserved).</summary>
    public void RemoveAt(int index)
    {
        int length = _header->Length;
        if ((uint)index >= (uint)length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        T* data = Data;
        int tail = length - index - 1;
        if (tail > 0)
        {
            Buffer.MemoryCopy(data + index + 1, data + index, (long)tail * sizeof(T), (long)tail * sizeof(T));
        }

        _header->Length = length - 1;
    }

    /// <summary>Removes the element at <paramref name="index"/> by moving the last element into its place (order not preserved, O(1)).</summary>
    public void RemoveAtSwapBack(int index)
    {
        int length = _header->Length;
        if ((uint)index >= (uint)length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        T* data = Data;
        data[index] = data[length - 1];
        _header->Length = length - 1;
    }

    /// <summary>Drops every element; keeps the storage.</summary>
    public void Clear() => _header->Length = 0;

    /// <summary>Sets the length; new elements are zeroed.</summary>
    public void Resize(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        int current = _header->Length;
        if (length > Capacity)
        {
            Grow(length);
        }

        if (length > current)
        {
            new Span<T>(Data + current, length - current).Clear();
        }

        _header->Length = length;
    }

    /// <summary>Makes room for <paramref name="capacity"/> elements without changing the length.</summary>
    public void EnsureCapacity(int capacity)
    {
        if (capacity > Capacity)
        {
            Grow(capacity);
        }
    }

    private void Grow(int required)
    {
        if (required > MaxLength)
        {
            throw new InvalidOperationException($"A buffer of {typeof(T).Name} holds at most {MaxLength} elements ({required} requested).");
        }

        int size = sizeof(T);
        int target = Math.Max(required, Math.Min(MaxLength, Capacity * 2));
        bool fromInline = IsInline;
        uint handle = _heap.Alloc(target * size, fromInline);
        T* source = Data;
        T* destination = (T*)_heap.Pointer(handle);
        long bytes = (long)_header->Length * size;
        Buffer.MemoryCopy(source, destination, bytes, bytes);
        if (!fromInline)
        {
            _heap.Free(_header->Handle);
        }

        _header->Handle = handle;
    }
}
