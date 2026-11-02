using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Colony.Fields;

/// <summary>
/// A row-span stencil body (TASK-43): computes the centre chunk through <see cref="StencilHalo{T}.Row"/> and
/// <see cref="StencilHalo{T}.OutputRow"/>. Pass it to <see cref="Stencil.DoubleBuffered{T}(string, Field{T}, StencilHaloKernel{T}, T, Phase?, IReadOnlyList{string}?, IReadOnlyList{string}?)"/>
/// or <see cref="Stencil.InPlace{T}(string, Field{T}, StencilHaloKernel{T}, T, Phase?, IReadOnlyList{string}?, IReadOnlyList{string}?)"/>.
/// </summary>
public delegate void StencilHaloKernel<T>(in StencilHalo<T> halo)
    where T : unmanaged;

/// <summary>Geometry of a <see cref="StencilHalo{T}"/>: the chunk plus one cell on each side, x fastest.</summary>
public static class StencilHalo
{
    /// <summary>Cells along each edge of the halo: <see cref="FieldChunk.Size"/> + 2, local coordinates <c>[-1, 17)</c>.</summary>
    public const int Side = FieldChunk.Size + 2;

    /// <summary>Index distance between (x, y, z) and (x, y + 1, z) in <see cref="StencilHalo{T}.Cells"/>.</summary>
    public const int RowStride = Side;

    /// <summary>Index distance between (x, y, z) and (x, y, z + 1) in <see cref="StencilHalo{T}.Cells"/>.</summary>
    public const int PlaneStride = Side * Side;

    /// <summary>Cells in the halo (18 x 18 x 18).</summary>
    public const int CellCount = Side * Side * Side;

    /// <summary>Index into <see cref="StencilHalo{T}.Cells"/> of local coordinates in <c>[-1, 17)</c> (not checked).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int IndexOf(int x, int y, int z) => (x + 1) + (RowStride * (y + 1)) + (PlaneStride * (z + 1));
}

/// <summary>
/// What a <see cref="StencilHaloKernel{T}"/> sees (TASK-43): the centre chunk and a one-cell ring around it,
/// gathered into an 18x18x18 block (<see cref="StencilHalo"/> geometry), with cells outside the World set to
/// <see cref="Boundary"/>. A kernel walks rows: <see cref="Row"/>(y, z) is the 18 cells of local x <c>-1..16</c>
/// (element <c>i</c> is local x <c>i - 1</c>), <see cref="OutputRow"/>(y, z) the 16 cells it writes.
/// </summary>
/// <remarks>
/// <para>Double-buffered mode: the halo is the previous state; output rows are a separate buffer, so every read
/// sees the state before this Tick's pass.</para>
/// <para>In-place ordered mode: the halo is the working buffer. <see cref="OutputRow"/>(y, z) aliases elements
/// <c>1..16</c> of <see cref="Row"/>(y, z), so a kernel sees its own earlier writes (exactly what
/// <see cref="StencilInput{T}.Read"/> shows in that mode); lower-coloured neighbours are already updated, and the
/// framework copies the centre back to the Field after the kernel returns.</para>
/// <para>Edge chunks (the last chunk along an axis of a World whose size is not a multiple of 16): cells past the
/// World bound are storage only. They always read as <see cref="Boundary"/>; <see cref="OutputRow"/> covers only the
/// in-World cells (shorter or empty rows), so a kernel cannot write them, and the framework stores
/// <see cref="Boundary"/> into them after the kernel returns. Kernels therefore loop x to <c>OutputRow(y, z).Length</c>.</para>
/// <para>Only the one-cell ring is available; a kernel reaching further uses <see cref="StencilKernel{T}"/>.</para>
/// </remarks>
public readonly ref struct StencilHalo<T>
    where T : unmanaged
{
    private readonly unsafe T* _cells;
    private readonly unsafe T* _output;

    internal unsafe StencilHalo(T* cells, T* output, ChunkCoord chunk, T boundary, int extentX, int extentY, int extentZ)
    {
        _cells = cells;
        _output = output;
        Chunk = chunk;
        Boundary = boundary;
        ExtentX = extentX;
        ExtentY = extentY;
        ExtentZ = extentZ;
    }

    /// <summary>The chunk whose cells the kernel is computing.</summary>
    public ChunkCoord Chunk { get; }

    /// <summary>Value of every cell outside the World.</summary>
    public T Boundary { get; }

    /// <summary>Whether output rows alias the halo (in-place ordered mode).</summary>
    public unsafe bool IsInPlace => _output is null;

    /// <summary>Centre cells along x that lie inside the World: 16 except on the last chunk of a World whose size is not a multiple of 16.</summary>
    public int ExtentX { get; }

    /// <summary>Centre cells along y that lie inside the World.</summary>
    public int ExtentY { get; }

    /// <summary>Centre cells along z that lie inside the World.</summary>
    public int ExtentZ { get; }

    /// <summary>The whole halo, indexed by <see cref="StencilHalo.IndexOf"/>.</summary>
    public unsafe ReadOnlySpan<T> Cells => new(_cells, StencilHalo.CellCount);

    /// <summary>The 18 halo cells of local x <c>-1..16</c> at local (y, z), each in <c>[-1, 17)</c>; element <c>i</c> is local x <c>i - 1</c>.</summary>
    public unsafe ReadOnlySpan<T> Row(int y, int z)
    {
        if ((uint)(y + 1) >= StencilHalo.Side || (uint)(z + 1) >= StencilHalo.Side)
        {
            ThrowRow(y, z);
        }

        return new ReadOnlySpan<T>(_cells + (StencilHalo.RowStride * (y + 1)) + (StencilHalo.PlaneStride * (z + 1)), StencilHalo.Side);
    }

    /// <summary>
    /// The output cells of local x <c>0..</c><see cref="ExtentX"/><c> - 1</c> at local (y, z), each in <c>[0, 16)</c>:
    /// 16 cells for an interior chunk, fewer on the last chunk along x, and empty for a row past the World bound in
    /// y or z. Loop to the span's <c>Length</c>.
    /// </summary>
    public unsafe Span<T> OutputRow(int y, int z)
    {
        if ((uint)y >= FieldChunk.Size || (uint)z >= FieldChunk.Size)
        {
            ThrowOutputRow(y, z);
        }

        T* start = _output is null
            ? _cells + 1 + (StencilHalo.RowStride * (y + 1)) + (StencilHalo.PlaneStride * (z + 1))
            : _output + FieldChunk.CellIndex(0, y, z);
        return new Span<T>(start, y < ExtentY && z < ExtentZ ? ExtentX : 0);
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowRow(int y, int z)
        => throw new ArgumentOutOfRangeException(nameof(y), $"Halo row ({y}, {z}) is outside the one-cell ring; y and z must be in [-1, {FieldChunk.Size + 1}).");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowOutputRow(int y, int z)
        => throw new ArgumentOutOfRangeException(nameof(y), $"Output row ({y}, {z}) is outside the chunk; y and z must be in [0, {FieldChunk.Size}).");
}
