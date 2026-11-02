namespace Colony.ProcGen;

/// <summary>
/// Which instantiation of the noise kernels runs. All three produce identical bits (the kernels are pure
/// integer arithmetic); the choice only affects speed. Internal: the public API picks the fastest accelerated
/// path, tests exercise all of them.
/// </summary>
internal enum KernelPath
{
    /// <summary>One lane at a time (<see cref="Lanes1"/>).</summary>
    Scalar,

    /// <summary>Four int32 lanes per <see cref="System.Runtime.Intrinsics.Vector128{T}"/>.</summary>
    Vector128,

    /// <summary>Eight int32 lanes per <see cref="System.Runtime.Intrinsics.Vector256{T}"/>.</summary>
    Vector256,
}
