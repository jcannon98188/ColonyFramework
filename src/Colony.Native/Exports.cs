using System.Runtime.InteropServices;
using Colony.Plugins.Native;

namespace Colony.Native;

/// <summary>The one export of the shared library (spec section 8): hands a C host the versioned function table.</summary>
public static unsafe class Exports
{
    /// <summary>
    /// <c>int colony_get_api(uint32_t version, const colony_api** out_api)</c>: <c>COLONY_OK</c> with the table when
    /// <paramref name="version"/> is <c>COLONY_ABI_VERSION</c>, <c>COLONY_VERSION_MISMATCH</c> otherwise (the table
    /// is still returned so the host can read its version word).
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "colony_get_api")]
    public static int GetApi(uint version, nint* outApi)
    {
        if (outApi == null)
        {
            return ColonyAbi.InvalidArgument;
        }

        *outApi = ColonyAbi.Table;
        return version == ColonyAbi.Version ? ColonyAbi.Ok : ColonyAbi.VersionMismatch;
    }
}
