using System.Reflection;
using BenchmarkDotNet.Attributes;

namespace Colony.Benchmarks.Tests;

/// <summary>
/// Ticket AC #1: the micro-benchmark suite covers storage, scheduler, Fields, hashing and save/load, and every
/// benchmark still runs. BenchmarkDotNet itself is not run here (long benchmark runs are not tests): each
/// benchmark class is set up with its first parameter value, every [Benchmark] method is invoked once, and the
/// class is cleaned up, exactly as the runner would do around its measurements.
/// </summary>
public sealed class BenchmarkSmokeTests
{
    private static readonly Assembly Benchmarks = typeof(StorageBenchmarks).Assembly;

    public static TheoryData<Type> BenchmarkClasses()
    {
        var data = new TheoryData<Type>();
        foreach (var type in Benchmarks.GetTypes().Where(t => t.IsClass && t.GetMethods().Any(m => m.GetCustomAttribute<BenchmarkAttribute>() is not null)).OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            data.Add(type);
        }

        return data;
    }

    [Fact]
    public void The_suite_covers_storage_scheduler_fields_hashing_save_load_and_the_diagnostics_overhead()
    {
        var names = BenchmarkClasses().Select(row => row.Data.Name).ToArray();

        Assert.Contains("StorageBenchmarks", names);
        Assert.Contains("SchedulerBenchmarks", names);
        Assert.Contains("FieldsBenchmarks", names);
        Assert.Contains("HashingBenchmarks", names);
        Assert.Contains("PersistenceBenchmarks", names);
        Assert.Contains("DiagnosticsOverheadBenchmarks", names);
    }

    [Theory]
    [MemberData(nameof(BenchmarkClasses))]
    public void Every_benchmark_sets_up_runs_once_and_cleans_up(Type benchmarkClass)
    {
        ArgumentNullException.ThrowIfNull(benchmarkClass);
        object instance = Activator.CreateInstance(benchmarkClass)!;
        foreach (var property in benchmarkClass.GetProperties())
        {
            if (property.GetCustomAttribute<ParamsAttribute>() is { } parameters)
            {
                property.SetValue(instance, parameters.Values[0]);
            }
        }

        Invoke<GlobalSetupAttribute>(benchmarkClass, instance);
        var benchmarks = benchmarkClass.GetMethods().Where(m => m.GetCustomAttribute<BenchmarkAttribute>() is not null).ToArray();
        Assert.NotEmpty(benchmarks);
        foreach (var benchmark in benchmarks)
        {
            benchmark.Invoke(instance, null);
        }

        Invoke<GlobalCleanupAttribute>(benchmarkClass, instance);
    }

    private static void Invoke<TAttribute>(Type type, object instance)
        where TAttribute : Attribute
    {
        foreach (var method in type.GetMethods().Where(m => m.GetCustomAttribute<TAttribute>() is not null))
        {
            method.Invoke(instance, null);
        }
    }
}
