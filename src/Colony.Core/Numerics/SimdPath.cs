namespace Colony.Numerics;

/// <summary>
/// Which instantiation of the float kernels runs. All three produce identical bits; the choice only affects speed.
/// Internal: the public API picks the fastest accelerated path, tests exercise all of them.
/// </summary>
internal enum SimdPath
{
    /// <summary>Plain scalar code (eight independent accumulators for reductions).</summary>
    Scalar,

    /// <summary>Two <see cref="System.Runtime.Intrinsics.Vector128{T}"/> halves per logical vector.</summary>
    Vector128,

    /// <summary>One <see cref="System.Runtime.Intrinsics.Vector256{T}"/> per logical vector.</summary>
    Vector256,
}
