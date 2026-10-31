using System.Runtime.InteropServices;
using Colony.Hashing;

namespace Colony.Fields;

/// <summary>
/// The CPU-authoritative <see cref="FieldStorage"/> (spec section 5.1): one 64-byte-aligned native block
/// of <see cref="FieldChunk.CellCount"/> cells per dense chunk, allocated the first time a chunk is written;
/// a uniform chunk is just its value in a per-chunk table. Nothing is static; many Worlds coexist.
/// </summary>
public sealed unsafe class CpuFieldStorage : FieldStorage
{
    private const int Alignment = 64;

    private readonly byte*[] _dense;
    private readonly byte[] _uniform;
    private readonly bool[] _isUniform;
    private readonly byte[] _expanded;
    private readonly nuint _chunkBytes;
    private long _allocated;
    private bool _disposed;

    public CpuFieldStorage(int cellSize, int chunkCount, ReadOnlySpan<byte> initialValue)
        : base(cellSize, chunkCount)
    {
        if (initialValue.Length != cellSize)
        {
            throw new ArgumentException($"The initial value is {initialValue.Length} bytes but a cell is {cellSize}.", nameof(initialValue));
        }

        _chunkBytes = (nuint)cellSize * FieldChunk.CellCount;
        _dense = new byte*[chunkCount];
        _uniform = new byte[(long)chunkCount * cellSize];
        _isUniform = new bool[chunkCount];
        _expanded = new byte[(int)_chunkBytes];
        Array.Fill(_isUniform, true);
        for (int ordinal = 0; ordinal < chunkCount; ordinal++)
        {
            initialValue.CopyTo(_uniform.AsSpan(ordinal * cellSize, cellSize));
        }
    }

    public override long CellBytesAllocated => _allocated;

    public override bool IsUniform(int ordinal) => _isUniform[ordinal];

    public override ReadOnlySpan<byte> UniformValue(int ordinal) => _uniform.AsSpan(ordinal * CellSize, CellSize);

    public override ReadOnlySpan<byte> ReadCells(int ordinal)
        => _isUniform[ordinal] ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>(_dense[ordinal], (int)_chunkBytes);

    public override Span<byte> WriteCells(int ordinal)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte* cells = _dense[ordinal];
        if (cells is null)
        {
            cells = (byte*)NativeMemory.AlignedAlloc(_chunkBytes, Alignment);
            _dense[ordinal] = cells;
            _allocated += (long)_chunkBytes;
        }

        var span = new Span<byte>(cells, (int)_chunkBytes);
        if (_isUniform[ordinal])
        {
            Expand(UniformValue(ordinal), span);
            _isUniform[ordinal] = false;
        }

        return span;
    }

    public override void Fill(int ordinal, ReadOnlySpan<byte> value)
    {
        if (value.Length != CellSize)
        {
            throw new ArgumentException($"The value is {value.Length} bytes but a cell is {CellSize}.", nameof(value));
        }

        value.CopyTo(_uniform.AsSpan(ordinal * CellSize, CellSize));
        _isUniform[ordinal] = true;
    }

    public override bool CellsEqual(int ordinal, ReadOnlySpan<byte> cells)
    {
        if (cells.Length != (int)_chunkBytes)
        {
            throw new ArgumentException($"Expected {_chunkBytes} bytes, got {cells.Length}.", nameof(cells));
        }

        if (!_isUniform[ordinal])
        {
            return cells.SequenceEqual(new ReadOnlySpan<byte>(_dense[ordinal], (int)_chunkBytes));
        }

        var value = UniformValue(ordinal);
        for (int offset = 0; offset < cells.Length; offset += CellSize)
        {
            if (!cells.Slice(offset, CellSize).SequenceEqual(value))
            {
                return false;
            }
        }

        return true;
    }

    public override void HashChunk(int ordinal, ref Hash64State state)
    {
        if (_isUniform[ordinal])
        {
            Expand(UniformValue(ordinal), _expanded);
            state.Append(_expanded);
        }
        else
        {
            state.Append(new ReadOnlySpan<byte>(_dense[ordinal], (int)_chunkBytes));
        }
    }

    /// <summary>Repeats one cell value over a whole chunk by doubling copies (log2 steps, no per-cell loop).</summary>
    private static void Expand(ReadOnlySpan<byte> value, Span<byte> cells)
    {
        value.CopyTo(cells);
        int filled = value.Length;
        while (filled < cells.Length)
        {
            int copy = Math.Min(filled, cells.Length - filled);
            cells[..copy].CopyTo(cells[filled..]);
            filled += copy;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (int i = 0; i < _dense.Length; i++)
        {
            if (_dense[i] is not null)
            {
                NativeMemory.AlignedFree(_dense[i]);
                _dense[i] = null;
            }
        }

        _allocated = 0;
    }

    ~CpuFieldStorage()
    {
        Dispose(disposing: false);
    }
}
