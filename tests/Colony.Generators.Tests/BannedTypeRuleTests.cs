namespace Colony.Generators.Tests;

/// <summary>
/// COL1002 (Vector&lt;T&gt;), COL1003 (wall-clock time), COL1004 (Guid.NewGuid) and COL1007 (System.Random):
/// any reference to the type or member inside System code is reported (spec section 6).
/// </summary>
public sealed class BannedTypeRuleTests
{
    [Fact]
    public void Vector_T_and_the_Vector_helper_class_are_reported()
    {
        AnalyzerHarness.Verify("""
            using System.Numerics;
            static class Flow
            {
                [Colony.System]
                public static {|COL1002:Vector<float>|} Step({|COL1002:Vector<float>|} a, float[] data)
                {
                    {|COL1002:var|} b = new {|COL1002:Vector<float>|}(data);
                    return {|COL1002:Vector|}.Add(a, b) * {|COL1002:Vector<float>|}.One;
                }
            }
            """);
    }

    [Fact]
    public void Vector128_and_user_types_named_Vector_are_allowed()
    {
        AnalyzerHarness.Verify("""
            using System.Runtime.Intrinsics;
            struct Vector<T> where T : struct { public T X; }
            static class Flow
            {
                [Colony.System]
                public static Vector128<float> Step(Vector128<float> a, Vector<float> v) => Vector128.Add(a, Vector128.Create(v.X));
            }
            """);
    }

    [Fact]
    public void Vector_T_outside_System_code_is_allowed()
    {
        AnalyzerHarness.Verify("""
            using System.Numerics;
            static class Rendering
            {
                public static Vector<float> Step(Vector<float> a) => Vector.Add(a, Vector<float>.One);
            }
            """);
    }

    [Fact]
    public void Wall_clock_time_sources_are_reported()
    {
        AnalyzerHarness.Verify("""
            using System;
            using System.Diagnostics;
            [Colony.System]
            static class Timing
            {
                public static long Now() => {|COL1003:DateTime|}.UtcNow.Ticks + {|COL1003:DateTimeOffset|}.Now.Ticks
                    + {|COL1003:Stopwatch|}.GetTimestamp() + {|COL1003:Environment.TickCount64|} + {|COL1003:Environment.TickCount|};
                public static void Measure({|COL1003:Stopwatch|} sw) => sw.Start();
                public static {|COL1003:DateTime|} Parse(string s) => {|COL1003:DateTime|}.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
            }
            """);
    }

    [Fact]
    public void Tick_counts_and_TimeSpan_arithmetic_are_allowed()
    {
        AnalyzerHarness.Verify("""
            using System;
            [Colony.System]
            static class Timing
            {
                public static TimeSpan Step(long tick, int ticksPerSecond) => TimeSpan.FromSeconds((double)tick / ticksPerSecond);
                public static string Machine() => Environment.MachineName;
            }
            """);
    }

    [Fact]
    public void Wall_clock_time_outside_System_code_is_allowed()
    {
        AnalyzerHarness.Verify("""
            using System;
            static class Host
            {
                public static long Now() => DateTime.UtcNow.Ticks + Environment.TickCount64;
            }
            """);
    }

    [Fact]
    public void Guid_NewGuid_is_reported_but_other_Guid_members_are_allowed()
    {
        AnalyzerHarness.Verify("""
            using System;
            [Colony.System]
            static class Ids
            {
                public static Guid Fresh() => {|COL1004:Guid.NewGuid|}();
                public static Guid Fixed() => Guid.Parse("8c1f9e3a-0f8b-4a19-9d3b-1b7f0f4c2d11");
                public static bool IsEmpty(Guid g) => g == Guid.Empty;
            }
            """);
    }

    [Fact]
    public void Guid_NewGuid_outside_System_code_is_allowed()
    {
        AnalyzerHarness.Verify("""
            using System;
            static class Host
            {
                public static Guid Fresh() => Guid.NewGuid();
            }
            """);
    }

    [Fact]
    public void System_Random_is_reported_wherever_it_appears()
    {
        AnalyzerHarness.Verify("""
            using System;
            [Colony.System]
            sealed class Weather
            {
                private readonly {|COL1007:Random|} _rng = new {|COL1007:Random|}(42);
                public int Roll() => _rng.Next(6) + {|COL1007:Random|}.Shared.Next();
                public int Roll({|COL1007:Random|} r) => r.Next();
            }
            """);
    }

    [Fact]
    public void Using_a_Random_obtained_from_outside_System_code_is_reported_at_the_use()
    {
        AnalyzerHarness.Verify("""
            using System;
            class Host
            {
                protected Random HostRng = new(1);
                protected Random GetRng() => HostRng;
            }
            [Colony.System]
            sealed class Weather : Host
            {
                public int Roll() => {|COL1007:HostRng.Next|}(6) + {|COL1007:GetRng().Next|}();
                public int RollLocal()
                {
                    {|COL1007:var|} local = GetRng();
                    return local.Next();
                }
            }
            """);
    }

    [Fact]
    public void A_user_type_named_Random_is_allowed()
    {
        AnalyzerHarness.Verify("""
            namespace Game
            {
                struct Random { public int Next() => 4; }
                [Colony.System]
                static class Weather
                {
                    public static int Roll(Random r) => r.Next();
                }
            }
            """);
    }

    [Fact]
    public void System_Random_outside_System_code_is_allowed()
    {
        AnalyzerHarness.Verify("""
            using System;
            static class Host
            {
                public static int Roll() => Random.Shared.Next(6);
            }
            """);
    }
}
