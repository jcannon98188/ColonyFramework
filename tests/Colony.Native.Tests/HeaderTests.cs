using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Colony.Plugins.Native;

namespace Colony.Native.Tests;

/// <summary>AC #3: colony.h is generated from the ABI definition and checked in; this is the check that fails when the two differ.</summary>
public sealed class HeaderTests
{
    public const string AcceptVariable = "COLONY_ACCEPT_HEADER";

    /// <summary>The committed header: <c>src/Colony.Native/colony.h</c>, located from this source file.</summary>
    public static string CommittedPath([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "Colony.Native", HeaderGenerator.FileName));

    [Fact]
    public void The_committed_header_matches_the_generated_one()
    {
        string path = CommittedPath();
        string generated = HeaderGenerator.Generate();
        string committed = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : string.Empty;
        if (generated != committed)
        {
            if (Environment.GetEnvironmentVariable(AcceptVariable) == "1")
            {
                File.WriteAllText(path, generated);
                return;
            }

            File.WriteAllText(path + ".received", generated);
            Assert.Fail($"{path} differs from the header generated from the ABI definition. Review {path}.received and accept with {AcceptVariable}=1.");
        }
    }

    [Fact]
    public void The_header_declares_every_table_entry_in_table_order()
    {
        string header = HeaderGenerator.Generate();
        int position = header.IndexOf("struct colony_api {", StringComparison.Ordinal);
        Assert.True(position > 0);
        foreach (var entry in AbiDefinition.Entries)
        {
            int next = header.IndexOf($"(*{entry.Name})(", position, StringComparison.Ordinal);
            Assert.True(next > position, $"{entry.Name} is missing or out of order in colony_api.");
            position = next;
        }

        Assert.Contains($"COLONY_API_COUNT = {AbiDefinition.Entries.Length}", header, StringComparison.Ordinal);
        Assert.Contains($"#define COLONY_ABI_VERSION {ColonyAbi.Version}u", header, StringComparison.Ordinal);
    }

    [Fact]
    public unsafe void The_function_table_holds_every_entry_address_after_the_version_and_size_words()
    {
        byte* table = (byte*)ColonyAbi.Table;
        Assert.Equal((uint)ColonyAbi.Version, *(uint*)table);
        Assert.Equal((uint)(8 + 8 * AbiDefinition.Entries.Length), *(uint*)(table + 4));
        nint* slots = (nint*)(table + 8);
        for (int i = 0; i < AbiDefinition.Entries.Length; i++)
        {
            Assert.Equal(AbiDefinition.Entries[i].Address, slots[i]);
            Assert.NotEqual(0, slots[i]);
        }
    }

    [Fact]
    public void Every_entry_names_an_UnmanagedCallersOnly_method_whose_parameter_count_matches_its_C_signature()
    {
        var methods = typeof(AbiEntries).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in AbiDefinition.Entries)
        {
            Assert.True(names.Add(entry.Name), $"duplicate entry {entry.Name}");
            var method = Assert.Single(methods, m => m.Name == entry.MethodName);
            Assert.NotNull(method.GetCustomAttributes(typeof(UnmanagedCallersOnlyAttribute), false).SingleOrDefault());
            Assert.Equal(entry.Parameters.Length, method.GetParameters().Length);
            Assert.Equal(typeof(int), method.ReturnType);
        }

        Assert.Equal(AbiDefinition.Entries.Length, methods.Length);
    }
}
