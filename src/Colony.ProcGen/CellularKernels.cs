namespace Colony.ProcGen;

/// <summary>
/// Cellular (Worley) noise kernels: one feature point per lattice cell, jittered by the cell's hash; F1 and F2 are
/// the Euclidean distances to the nearest and second-nearest feature over the 3^n neighbourhood, and the cell is
/// the nearest feature's hash (a stable id for "which cell am I in" effects). Distances are square-rooted with an
/// integer digit-by-digit root in Q28, so they carry 14 fractional bits (quantum 2^-14) and saturate at sqrt(8).
/// </summary>
internal static class CellularKernels
{
    private const int MaxDistanceSquared = (8 << 16) - 1;

    internal static void Cellular2<T>(T keyLow, T keyHigh, T x, T y, out T f1, out T f2, out T cell)
        where T : struct, IIntLanes<T>
    {
        var ix = NoiseKernels.Floor(x);
        var iy = NoiseKernels.Floor(y);
        var fx = NoiseKernels.Fraction(x);
        var fy = NoiseKernels.Fraction(y);
        var mask16 = T.Broadcast(0xFFFF);
        var best = T.Broadcast(int.MaxValue);
        var second = best;
        var bestCell = T.Zero;

        for (int dj = -1; dj <= 1; dj++)
        {
            var cy = T.Add(iy, T.Broadcast(dj));
            var oy = T.Subtract(T.Broadcast(dj << 16), fy);
            for (int di = -1; di <= 1; di++)
            {
                var cx = T.Add(ix, T.Broadcast(di));
                var h = NoiseKernels.Hash2(keyLow, keyHigh, cx, cy);
                var dx = T.Add(T.Subtract(T.Broadcast(di << 16), fx), T.And(h, mask16));
                var dy = T.Add(oy, T.ShiftRightLogical(h, 16));
                var d2 = T.Add(T.MultiplyQ16(dx, dx), T.MultiplyQ16(dy, dy));
                Accumulate(h, d2, ref best, ref second, ref bestCell);
            }
        }

        f1 = Distance(best);
        f2 = Distance(second);
        cell = bestCell;
    }

    internal static void Cellular3<T>(T keyLow, T keyHigh, T x, T y, T z, out T f1, out T f2, out T cell)
        where T : struct, IIntLanes<T>
    {
        var ix = NoiseKernels.Floor(x);
        var iy = NoiseKernels.Floor(y);
        var iz = NoiseKernels.Floor(z);
        var fx = NoiseKernels.Fraction(x);
        var fy = NoiseKernels.Fraction(y);
        var fz = NoiseKernels.Fraction(z);
        var mask11 = T.Broadcast(0x7FF);
        var best = T.Broadcast(int.MaxValue);
        var second = best;
        var bestCell = T.Zero;

        for (int dk = -1; dk <= 1; dk++)
        {
            var cz = T.Add(iz, T.Broadcast(dk));
            var oz = T.Subtract(T.Broadcast(dk << 16), fz);
            for (int dj = -1; dj <= 1; dj++)
            {
                var cy = T.Add(iy, T.Broadcast(dj));
                var oy = T.Subtract(T.Broadcast(dj << 16), fy);
                for (int di = -1; di <= 1; di++)
                {
                    var cx = T.Add(ix, T.Broadcast(di));
                    var h = NoiseKernels.Hash3(keyLow, keyHigh, cx, cy, cz);
                    // 11 + 11 + 10 hash bits of jitter, each scaled to Q16.
                    var dx = T.Add(T.Subtract(T.Broadcast(di << 16), fx), T.ShiftLeft(T.And(h, mask11), 5));
                    var dy = T.Add(oy, T.ShiftLeft(T.And(T.ShiftRightLogical(h, 11), mask11), 5));
                    var dz = T.Add(oz, T.ShiftLeft(T.ShiftRightLogical(h, 22), 6));
                    var d2 = T.Add(T.Add(T.MultiplyQ16(dx, dx), T.MultiplyQ16(dy, dy)), T.MultiplyQ16(dz, dz));
                    Accumulate(h, d2, ref best, ref second, ref bestCell);
                }
            }
        }

        f1 = Distance(best);
        f2 = Distance(second);
        cell = bestCell;
    }

    private static void Accumulate<T>(T hash, T distanceSquared, ref T best, ref T second, ref T bestCell)
        where T : struct, IIntLanes<T>
    {
        var closer = T.LessThan(distanceSquared, best);
        second = T.Select(closer, best, T.Min(second, distanceSquared));
        bestCell = T.Select(closer, hash, bestCell);
        best = T.Min(best, distanceSquared);
    }

    /// <summary>Q16 Euclidean distance from a Q16 squared distance, saturating at sqrt(8).</summary>
    private static T Distance<T>(T distanceSquared)
        where T : struct, IIntLanes<T>
    {
        var clamped = T.Min(distanceSquared, T.Broadcast(MaxDistanceSquared));
        return T.ShiftLeft(IntegerSqrt(T.ShiftLeft(clamped, 12)), 2);
    }

    /// <summary>Floor of the square root of a non-negative int32, digit by digit (16 fixed iterations).</summary>
    internal static T IntegerSqrt<T>(T value)
        where T : struct, IIntLanes<T>
    {
        var remainder = value;
        var root = T.Zero;
        for (int bit = 30; bit >= 0; bit -= 2)
        {
            var trial = T.Add(root, T.Broadcast(1 << bit));
            var fits = T.AndNot(T.Broadcast(-1), T.LessThan(remainder, trial)); // remainder >= trial
            remainder = T.Select(fits, T.Subtract(remainder, trial), remainder);
            root = T.Add(T.ShiftRightLogical(root, 1), T.And(fits, T.Broadcast(1 << bit)));
        }

        return root;
    }
}
