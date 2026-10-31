using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

// Without arguments every benchmark runs; BenchmarkDotNet options (such as a filter) pass straight through.
var config = ManualConfig.Create(DefaultConfig.Instance)
    .AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance).WithWarmupCount(3).WithIterationCount(10).WithId("InProcess"))
    .AddDiagnoser(MemoryDiagnoser.Default)
    .AddExporter(JsonExporter.Full);

string[] effective = args.Length == 0 ? ["--filter", "*"] : args;
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(effective, config);
return 0;

/// <summary>Anchor type for <see cref="BenchmarkSwitcher.FromAssembly"/>.</summary>
public static partial class Program
{
}
