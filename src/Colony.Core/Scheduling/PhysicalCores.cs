using System.Runtime.InteropServices;

namespace Colony;

/// <summary>
/// Physical core count for the default worker count (spec section 4.3; the prototype showed that filling
/// every SMT sibling slows Ticks down). Windows asks the kernel for processor-core relationships; Linux
/// counts distinct (package, core) pairs in sysfs; anything else falls back to the logical count.
/// </summary>
internal static partial class PhysicalCores
{
    private const int RelationProcessorCore = 0;
    private const int ErrorInsufficientBuffer = 122;

    public static int Count()
    {
        int logical = Environment.ProcessorCount;
        int physical = 0;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                physical = CountWindows();
            }
            else if (OperatingSystem.IsLinux())
            {
                physical = CountLinux();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EntryPointNotFoundException or DllNotFoundException)
        {
            physical = 0;
        }

        return physical > 0 ? Math.Min(physical, logical) : logical;
    }

    private static unsafe int CountWindows()
    {
        uint length = 0;
        if (GetLogicalProcessorInformationEx(RelationProcessorCore, null, ref length) || Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer || length == 0)
        {
            return 0;
        }

        byte[] buffer = new byte[length];
        fixed (byte* p = buffer)
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, p, ref length))
            {
                return 0;
            }

            int cores = 0;
            byte* cursor = p;
            byte* end = p + length;
            while (cursor < end)
            {
                // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX starts with: LOGICAL_PROCESSOR_RELATIONSHIP Relationship (4), DWORD Size (4).
                int relationship = *(int*)cursor;
                uint size = *(uint*)(cursor + 4);
                if (size == 0)
                {
                    return 0;
                }

                if (relationship == RelationProcessorCore)
                {
                    cores++;
                }

                cursor += size;
            }

            return cores;
        }
    }

    private static int CountLinux()
    {
        const string root = "/sys/devices/system/cpu";
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var seen = new HashSet<(string Package, string Core)>();
        foreach (string dir in Directory.EnumerateDirectories(root, "cpu*"))
        {
            string coreFile = Path.Combine(dir, "topology", "core_id");
            string packageFile = Path.Combine(dir, "topology", "physical_package_id");
            if (File.Exists(coreFile) && File.Exists(packageFile))
            {
                seen.Add((File.ReadAllText(packageFile).Trim(), File.ReadAllText(coreFile).Trim()));
            }
        }

        return seen.Count;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetLogicalProcessorInformationEx(int relationshipType, byte* buffer, ref uint returnedLength);
}
