using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using GpuField.Fields;

namespace GpuField.Kernels;

/// <summary>
/// The update rule of a diffusion Kernel. CPU scalar, Vector128 and Vector256 forms must agree exactly
/// with each other (and, for Exact kernels, with the GPU WGSL form).
/// </summary>
public interface IStencilRule<T> where T : unmanaged
{
    static abstract T Scalar(T c, T xm, T xp, T ym, T yp, T zm, T zp);
    static abstract Vector128<T> V128(Vector128<T> c, Vector128<T> xm, Vector128<T> xp, Vector128<T> ym, Vector128<T> yp, Vector128<T> zm, Vector128<T> zp);
    static abstract Vector256<T> V256(Vector256<T> c, Vector256<T> xm, Vector256<T> xp, Vector256<T> ym, Vector256<T> yp, Vector256<T> zm, Vector256<T> zp);
    /// <summary>WGSL expression over c, xm, xp, ym, yp, zm, zp.</summary>
    static abstract string Wgsl { get; }
    static abstract string WgslType { get; }
}

/// <summary>Exact Kernel class: int32 heat, c' = c + ((sum6 - 6c) >> 3). Wrapping adds, arithmetic shift. Bit-identical everywhere.</summary>
public struct ExactRule : IStencilRule<int>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Scalar(int c, int xm, int xp, int ym, int yp, int zm, int zp)
        => unchecked(c + ((xm + xp + ym + yp + zm + zp - 6 * c) >> 3));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> V128(Vector128<int> c, Vector128<int> xm, Vector128<int> xp, Vector128<int> ym, Vector128<int> yp, Vector128<int> zm, Vector128<int> zp)
        => c + Vector128.ShiftRightArithmetic(xm + xp + ym + yp + zm + zp - c * 6, 3);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<int> V256(Vector256<int> c, Vector256<int> xm, Vector256<int> xp, Vector256<int> ym, Vector256<int> yp, Vector256<int> zm, Vector256<int> zp)
        => c + Vector256.ShiftRightArithmetic(xm + xp + ym + yp + zm + zp - c * 6, 3);
    public static string Wgsl => "c + ((xm + xp + ym + yp + zm + zp - 6 * c) >> 3u)";
    public static string WgslType => "i32";
}

/// <summary>Approximate Kernel class: float heat, c' = c + (sum6 - 6c) * 0.125. Same op order everywhere, but GPU may contract to FMA.</summary>
public struct ApproxRule : IStencilRule<float>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Scalar(float c, float xm, float xp, float ym, float yp, float zm, float zp)
        => c + (((((xm + xp) + ym) + yp) + zm) + zp - 6f * c) * 0.125f;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> V128(Vector128<float> c, Vector128<float> xm, Vector128<float> xp, Vector128<float> ym, Vector128<float> yp, Vector128<float> zm, Vector128<float> zp)
        => c + (((((xm + xp) + ym) + yp) + zm) + zp - Vector128.Create(6f) * c) * Vector128.Create(0.125f);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<float> V256(Vector256<float> c, Vector256<float> xm, Vector256<float> xp, Vector256<float> ym, Vector256<float> yp, Vector256<float> zm, Vector256<float> zp)
        => c + (((((xm + xp) + ym) + yp) + zm) + zp - Vector256.Create(6f) * c) * Vector256.Create(0.125f);
    public static string Wgsl => "c + ((((((xm + xp) + ym) + yp) + zm) + zp) - 6.0 * c) * 0.125";
    public static string WgslType => "f32";
}

public enum SimdWidth { Scalar, V128, V256 }

/// <summary>CPU Backend for the diffusion Kernel: reference implementation and SIMD baseline.</summary>
public static unsafe class CpuDiffusion<T, TRule> where T : unmanaged where TRule : struct, IStencilRule<T>
{
    readonly struct Nb(T* p, int rowMul)
    {
        public readonly T* P = p;       // null: outside the World (clamp to self)
        public readonly int RowMul = rowMul; // 1: real chunk, 0: uniform row repeated
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T* Row(int z, int y) => P + ((z * 16 + y) * 16) * RowMul;
    }

    static Nb Neighbour(ChunkedField<T> f, T* pool, int cx, int cy, int cz)
    {
        if ((uint)cx >= (uint)f.Cx || (uint)cy >= (uint)f.Cy || (uint)cz >= (uint)f.Cz) return new Nb(null, 0);
        int id = f.ChunkId(cx, cy, cz);
        int s = f.SlotOf[id];
        return s < 0 ? new Nb(f.UniformRows + (nint)id * 16, 0) : new Nb(f.ChunkPtr(pool, s), 1);
    }

    public static SimdWidth BestWidth =>
        Vector256.IsHardwareAccelerated && Vector256<T>.IsSupported ? SimdWidth.V256 :
        Vector128.IsHardwareAccelerated && Vector128<T>.IsSupported ? SimdWidth.V128 : SimdWidth.Scalar;

    /// <summary>One Tick of the Kernel over every allocated chunk: reads Cur, writes Next, swaps.</summary>
    public static void Step(ChunkedField<T> f, SimdWidth width, bool parallel, int chunksPerTask = 8)
    {
        if (parallel)
        {
            int tasks = (f.SlotCount + chunksPerTask - 1) / chunksPerTask;
            Parallel.For(0, tasks, t =>
            {
                int end = Math.Min(f.SlotCount, (t + 1) * chunksPerTask);
                for (int s = t * chunksPerTask; s < end; s++) StepChunk(f, s, width);
            });
        }
        else
        {
            for (int s = 0; s < f.SlotCount; s++) StepChunk(f, s, width);
        }
        f.Swap();
    }

    public static void StepChunk(ChunkedField<T> f, int slot, SimdWidth width)
    {
        T* src = f.ChunkPtr(f.Cur, slot), dst = f.ChunkPtr(f.Next, slot);
        var (cx, cy, cz) = f.ChunkCoord(f.ChunkOfSlot[slot]);
        Nb xmN = Neighbour(f, f.Cur, cx - 1, cy, cz), xpN = Neighbour(f, f.Cur, cx + 1, cy, cz);
        Nb ymN = Neighbour(f, f.Cur, cx, cy - 1, cz), ypN = Neighbour(f, f.Cur, cx, cy + 1, cz);
        Nb zmN = Neighbour(f, f.Cur, cx, cy, cz - 1), zpN = Neighbour(f, f.Cur, cx, cy, cz + 1);

        for (int z = 0; z < 16; z++)
        for (int y = 0; y < 16; y++)
        {
            int off = (z * 16 + y) * 16;
            T* c = src + off;
            T* ym = y > 0 ? c - 16 : ymN.P == null ? c : ymN.Row(z, 15);
            T* yp = y < 15 ? c + 16 : ypN.P == null ? c : ypN.Row(z, 0);
            T* zm = z > 0 ? c - 256 : zmN.P == null ? c : zmN.Row(15, y);
            T* zp = z < 15 ? c + 256 : zpN.P == null ? c : zpN.Row(0, y);
            T left = xmN.P == null ? c[0] : xmN.Row(z, y)[15];
            T right = xpN.P == null ? c[15] : xpN.Row(z, y)[0];
            T* d = dst + off;

            switch (width)
            {
                case SimdWidth.V256: Row256(c, ym, yp, zm, zp, left, right, d); break;
                case SimdWidth.V128: Row128(c, ym, yp, zm, zp, left, right, d); break;
                default: RowScalar(c, ym, yp, zm, zp, left, right, d); break;
            }
        }
    }

    static readonly Vector256<T> Lane0Mask256 = Vector256.Create(-1, 0, 0, 0, 0, 0, 0, 0).As<int, T>();
    static readonly Vector256<T> Lane7Mask256 = Vector256.Create(0, 0, 0, 0, 0, 0, 0, -1).As<int, T>();
    static readonly Vector128<T> Lane0Mask128 = Vector128.Create(-1, 0, 0, 0).As<int, T>();
    static readonly Vector128<T> Lane3Mask128 = Vector128.Create(0, 0, 0, -1).As<int, T>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Row256(T* c, T* ym, T* yp, T* zm, T* zp, T left, T right, T* d)
    {
        // Row of 16 = two 8-lane halves. x-1 / x+1 are unaligned loads one element off; the row-end lanes are
        // replaced with the neighbour (or clamped) values. Pools are padded so the off-by-one loads stay in bounds.
        var c0 = Vector256.Load(c); var c1 = Vector256.Load(c + 8);
        var xm0 = Vector256.ConditionalSelect(Lane0Mask256, Vector256.Create(left), Vector256.Load(c - 1));
        var xm1 = Vector256.Load(c + 7);
        var xp0 = Vector256.Load(c + 1);
        var xp1 = Vector256.ConditionalSelect(Lane7Mask256, Vector256.Create(right), Vector256.Load(c + 9));
        TRule.V256(c0, xm0, xp0, Vector256.Load(ym), Vector256.Load(yp), Vector256.Load(zm), Vector256.Load(zp)).Store(d);
        TRule.V256(c1, xm1, xp1, Vector256.Load(ym + 8), Vector256.Load(yp + 8), Vector256.Load(zm + 8), Vector256.Load(zp + 8)).Store(d + 8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Row128(T* c, T* ym, T* yp, T* zm, T* zp, T left, T right, T* d)
    {
        for (int h = 0; h < 16; h += 4)
        {
            var cv = Vector128.Load(c + h);
            var xm = Vector128.Load(c + h - 1);
            var xp = Vector128.Load(c + h + 1);
            if (h == 0) xm = Vector128.ConditionalSelect(Lane0Mask128, Vector128.Create(left), xm);
            if (h == 12) xp = Vector128.ConditionalSelect(Lane3Mask128, Vector128.Create(right), xp);
            TRule.V128(cv, xm, xp, Vector128.Load(ym + h), Vector128.Load(yp + h), Vector128.Load(zm + h), Vector128.Load(zp + h)).Store(d + h);
        }
    }

    static void RowScalar(T* c, T* ym, T* yp, T* zm, T* zp, T left, T right, T* d)
    {
        for (int x = 0; x < 16; x++)
            d[x] = TRule.Scalar(c[x], x == 0 ? left : c[x - 1], x == 15 ? right : c[x + 1], ym[x], yp[x], zm[x], zp[x]);
    }

    /// <summary>The oracle's oracle: straightforward per-cell World-coordinate reference, independent of the chunk-row code.</summary>
    public static void StepReference(ChunkedField<T> f)
    {
        for (int s = 0; s < f.SlotCount; s++)
        {
            var (cx, cy, cz) = f.ChunkCoord(f.ChunkOfSlot[s]);
            for (int lz = 0; lz < 16; lz++)
            for (int ly = 0; ly < 16; ly++)
            for (int lx = 0; lx < 16; lx++)
            {
                int x = cx * 16 + lx, y = cy * 16 + ly, z = cz * 16 + lz;
                T c = f.Get(f.Cur, x, y, z);
                T G(int gx, int gy, int gz) =>
                    gx < 0 || gy < 0 || gz < 0 || gx >= f.SizeX || gy >= f.SizeY || gz >= f.SizeZ ? c : f.Get(f.Cur, gx, gy, gz);
                f.Set(f.Next, x, y, z, TRule.Scalar(c, G(x - 1, y, z), G(x + 1, y, z), G(x, y - 1, z), G(x, y + 1, z), G(x, y, z - 1), G(x, y, z + 1)));
            }
        }
        f.Swap();
    }
}
