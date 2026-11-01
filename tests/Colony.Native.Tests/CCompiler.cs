using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Colony.Hashing;

namespace Colony.Native.Tests;

/// <summary>
/// Compiles the conformance C source with whatever C compiler the machine has: MSVC (located through vswhere, run
/// under vcvars64) on Windows, <c>cc</c>/<c>gcc</c>/<c>clang</c> elsewhere. Outputs land in
/// <c>artifacts/native-tests/</c> keyed by a hash of the source, the header and the defines, so unchanged inputs
/// compile once. Returns null when no compiler is available (tests then <c>Assert.Skip</c>).
/// </summary>
internal static class CCompiler
{
    private static readonly Lock Gate = new();
    private static string? _toolchain;
    private static bool _searched;

    public static string RepositoryRoot([CallerFilePath] string here = "")
    {
        string? dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Colony.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Colony.slnx not found above " + here);
    }

    public static string ConformanceSource => Path.Combine(RepositoryRoot(), "tests", "Colony.Native.Tests", "Conformance", "conformance.c");

    public static string IncludeDirectory => Path.Combine(RepositoryRoot(), "src", "Colony.Native");

    /// <summary>The compiler driver ("cl" via vcvars64, or a Unix cc), or null when none is installed.</summary>
    public static string? Toolchain
    {
        get
        {
            lock (Gate)
            {
                if (!_searched)
                {
                    _searched = true;
                    _toolchain = Locate();
                }

                return _toolchain;
            }
        }
    }

    /// <summary>Builds the conformance source as a shared library; null when no compiler is available.</summary>
    public static string? BuildSharedLibrary(string name) => Build(name, shared: true, defines: []);

    /// <summary>Builds the conformance source as the standalone executable; null when no compiler is available.</summary>
    public static string? BuildExecutable(string name) => Build(name, shared: false, defines: ["CONFORMANCE_STANDALONE"]);

    private static string? Build(string name, bool shared, string[] defines)
    {
        string? toolchain = Toolchain;
        if (toolchain is null)
        {
            return null;
        }

        string source = ConformanceSource;
        string header = Path.Combine(IncludeDirectory, "colony.h");
        ulong key = Hash64.Compute(Encoding.UTF8.GetBytes(File.ReadAllText(source) + File.ReadAllText(header) + string.Join(';', defines) + toolchain + (shared ? "shared" : "exe")), 0x4E41_5449UL);
        string directory = Path.Combine(RepositoryRoot(), "artifacts", "native-tests");
        Directory.CreateDirectory(directory);
        string stem = $"{name}-{key:x16}";
        string extension = shared ? (OperatingSystem.IsWindows() ? ".dll" : ".so") : (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
        string output = Path.Combine(directory, stem + extension);
        lock (Gate)
        {
            if (File.Exists(output))
            {
                return output;
            }

            string log = toolchain == "cl" ? CompileMsvc(source, output, directory, stem, shared, defines) : CompileUnix(toolchain, source, output, shared, defines);
            if (!File.Exists(output))
            {
                throw new InvalidOperationException($"C compilation of {source} failed:\n{log}");
            }

            return output;
        }
    }

    private static string CompileMsvc(string source, string output, string directory, string stem, bool shared, string[] defines)
    {
        string vcvars = Vcvars64() ?? throw new InvalidOperationException("vcvars64.bat not found.");
        var args = new StringBuilder();
        args.Append($"/nologo /W4 /WX /O2 /std:c11 /I\"{IncludeDirectory}\" ");
        foreach (string define in defines)
        {
            args.Append($"/D{define} ");
        }

        if (shared)
        {
            args.Append("/LD ");
        }

        args.Append($"\"{source}\" /Fe:\"{output}\" /Fo:\"{Path.Combine(directory, stem + ".obj")}\"");
        // cmd /s /c strips the outer quotes and runs the whole string; vcvars sets INCLUDE/LIB/PATH for cl and link.
        string command = $"\"\"{vcvars}\" >nul && cl {args}\"";
        return Run("cmd.exe", $"/s /c {command}", directory);
    }

    private static string CompileUnix(string compiler, string source, string output, bool shared, string[] defines)
    {
        var args = new StringBuilder($"-std=c11 -O2 -Wall -Wextra -Werror -fPIC -I\"{IncludeDirectory}\" ");
        foreach (string define in defines)
        {
            args.Append($"-D{define} ");
        }

        if (shared)
        {
            args.Append("-shared ");
        }

        args.Append($"-o \"{output}\" \"{source}\"");
        if (!shared)
        {
            args.Append(" -ldl");
        }

        return Run(compiler, args.ToString(), Path.GetDirectoryName(output)!);
    }

    private static string? Locate()
    {
        if (OperatingSystem.IsWindows())
        {
            return Vcvars64() is null ? null : "cl";
        }

        foreach (string candidate in new[] { "cc", "gcc", "clang" })
        {
            try
            {
                using var probe = Process.Start(new ProcessStartInfo(candidate, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                probe!.WaitForExit();
                if (probe.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // not installed
            }
        }

        return null;
    }

    private static string? Vcvars64()
    {
        string vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere))
        {
            return null;
        }

        string found = Run(vswhere, "-latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find VC\\Auxiliary\\Build\\vcvars64.bat", Path.GetTempPath()).Trim();
        string? path = found.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).FirstOrDefault(File.Exists);
        return path;
    }

    private static string Run(string fileName, string arguments, string workingDirectory)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        }) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return stdout + stderr;
    }
}
