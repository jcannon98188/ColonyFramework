using System.Diagnostics;

namespace GpuField.Bench;

public static class Stats
{
    public static string Fmt(IReadOnlyList<double> ms)
    {
        if (ms.Count == 0) return "n/a";
        var s = ms.OrderBy(x => x).ToArray();
        double P(double q) => s[Math.Min(s.Length - 1, (int)Math.Ceiling(q * s.Length) - 1)];
        return $"mean {ms.Average(),8:F3} | p50 {P(0.50),8:F3} | p99 {P(0.99),8:F3} | min {s[0],8:F3} | max {s[^1],8:F3} ms (n={s.Length})";
    }

    public static double Mean(IReadOnlyList<double> ms) => ms.Average();

    /// <summary>Runs `warmup` untimed and `iters` timed calls; returns the per-call wall times in ms.</summary>
    public static List<double> Time(int warmup, int iters, Action a)
    {
        for (int i = 0; i < warmup; i++) a();
        var r = new List<double>(iters);
        for (int i = 0; i < iters; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            a();
            r.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }
        return r;
    }

    public static void Line(string label, IReadOnlyList<double> ms) => Console.WriteLine($"{label,-58} {Fmt(ms)}");
}
