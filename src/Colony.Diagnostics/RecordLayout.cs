using System.Globalization;

namespace Colony.Diagnostics;

/// <summary>
/// A sink in this package was compiled for one <see cref="TickRecord.LayoutVersion"/> and the Colony.Core it
/// runs against writes another (spec section 10.1: the record layout is versioned so sinks fail clearly
/// instead of misreading fields). Rebuild Colony.Diagnostics against the Core in use.
/// </summary>
public sealed class RecordLayoutMismatchException : InvalidOperationException
{
    public RecordLayoutMismatchException()
    {
    }

    public RecordLayoutMismatchException(string message)
        : base(message)
    {
    }

    public RecordLayoutMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal RecordLayoutMismatchException(string sink, int expected, int actual)
        : base(string.Create(
            CultureInfo.InvariantCulture,
            $"The {sink} was compiled for diagnostics record layout version {expected} but this Colony.Core writes version {actual}; rebuild Colony.Diagnostics against the Colony.Core in use."))
    {
        ExpectedVersion = expected;
        ActualVersion = actual;
    }

    /// <summary>The layout version the sink was compiled for.</summary>
    public int ExpectedVersion { get; }

    /// <summary>The layout version found in the records.</summary>
    public int ActualVersion { get; }
}

/// <summary>
/// The record layout version this assembly was compiled against. <see cref="TickRecord.LayoutVersion"/> is a
/// constant, so the C# compiler bakes its value into this assembly; comparing it with what Core reports at
/// run time (<see cref="WorldDiagnostics.LayoutVersion"/>, <see cref="TickRecord.Version"/>) catches a
/// Colony.Core upgrade that changed the records under a stale Colony.Diagnostics.
/// </summary>
internal static class RecordLayout
{
    public const int CompiledVersion = TickRecord.LayoutVersion;

    /// <summary>Throws <see cref="RecordLayoutMismatchException"/> when <paramref name="actualVersion"/> is not the compiled version.</summary>
    public static void Verify(int actualVersion, string sink)
    {
        if (actualVersion != CompiledVersion)
        {
            throw new RecordLayoutMismatchException(sink, CompiledVersion, actualVersion);
        }
    }

    /// <summary>Checks the version Core reports for <paramref name="world"/>.</summary>
    public static void Verify(World world, string sink)
    {
        ArgumentNullException.ThrowIfNull(world);
        Verify(WorldDiagnostics.LayoutVersion, sink);
    }
}
