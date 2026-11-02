using System.Runtime.InteropServices;

namespace Colony.Plugins.Native;

/// <summary>
/// The C ABI host (spec section 8): the versioned function table (<c>colony_api</c> in <c>colony.h</c>) that a C
/// host or a Native Plugin calls into. The table is one block of <c>uint32 version, uint32 size</c> followed by
/// the entry addresses in <see cref="AbiDefinition.Entries"/> order, which is exactly the struct the header
/// declares. Colony.Native exports <c>colony_get_api</c> over <see cref="Table"/>; a managed host that loads C code
/// in-process (the conformance suite on JIT) passes <see cref="Table"/> itself.
/// </summary>
public static unsafe class ColonyAbi
{
    /// <summary><c>COLONY_ABI_VERSION</c>: the version a Native Plugin records in its manifest and saves (spec section 8).</summary>
    public const int Version = ColonyApi.AbiVersion;

    /// <summary><c>COLONY_OK</c>.</summary>
    public const int Ok = 0;

    /// <summary><c>COLONY_ERROR</c>: the operation threw; <c>colony_last_error</c> has the message.</summary>
    public const int Error = 1;

    /// <summary><c>COLONY_INVALID_ARGUMENT</c>: a null pointer, a dead handle, a size mismatch or an id the schema does not have.</summary>
    public const int InvalidArgument = 2;

    /// <summary><c>COLONY_NOT_FOUND</c>: a name lookup found nothing.</summary>
    public const int NotFound = 3;

    /// <summary><c>COLONY_VERSION_MISMATCH</c>: <c>colony_get_api</c> was asked for another ABI version.</summary>
    public const int VersionMismatch = 4;

    // Process-wide by design (spec section 3 exception, ADR 0005): the C errno convention, per thread, so
    // concurrent Worlds on different threads never see each other's messages. Never simulation state.
    [ThreadStatic]
    private static string? _lastError;

    private static readonly nint TableBlock = BuildTable();

    /// <summary>The address of the process-wide <c>colony_api</c> table (never freed).</summary>
    public static nint Table => TableBlock;

    /// <summary>Entries in the table, excluding the version and size words.</summary>
    public static int EntryCount => AbiDefinition.Entries.Length;

    /// <summary>The message of the last failing call on this thread (what <c>colony_last_error</c> returns).</summary>
    public static string LastError => _lastError ?? string.Empty;

    internal static int Fail(Exception e)
    {
        _lastError = e.Message;
        return e switch
        {
            ArgumentException => InvalidArgument,
            KeyNotFoundException => NotFound,
            _ => Error,
        };
    }

    internal static int Invalid(string message)
    {
        _lastError = message;
        return InvalidArgument;
    }

    private static nint BuildTable()
    {
        var entries = AbiDefinition.Entries;
        nuint size = (nuint)(2 * sizeof(uint) + entries.Length * sizeof(nint));
        byte* block = (byte*)NativeMemory.AllocZeroed(size);
        *(uint*)block = Version;
        *(uint*)(block + sizeof(uint)) = (uint)size;
        nint* slots = (nint*)(block + 2 * sizeof(uint));
        for (int i = 0; i < entries.Length; i++)
        {
            slots[i] = entries[i].Address;
        }

        return (nint)block;
    }
}
