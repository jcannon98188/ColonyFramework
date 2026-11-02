using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Colony;

/// <summary>
/// The natural alignment of an unmanaged type, measured from its runtime layout (the offset of a
/// <typeparamref name="T"/> after one byte). Shared by <see cref="WorldBuilder"/> and the Plugins
/// definition registry (ADR 0005). <c>Colony.Fields</c> keeps its own copy: it has no access to Core
/// internals, and the helper is not worth public API.
/// </summary>
/// <typeparam name="T">The type to measure.</typeparam>
internal static class NaturalAlignment<T>
    where T : unmanaged
{
    /// <summary>The alignment in bytes (1, 2, 4, 8 or 16).</summary>
    public static int Value => Unsafe.SizeOf<Probe>() - Unsafe.SizeOf<T>();

    /// <summary>The offset of the value equals the natural alignment of <typeparamref name="T"/>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Probe
    {
#pragma warning disable CS0169, CA1823, IDE0051 // Fields are read by the layout only.
        private byte _pad;
        private T _value;
#pragma warning restore CS0169, CA1823, IDE0051
    }
}
