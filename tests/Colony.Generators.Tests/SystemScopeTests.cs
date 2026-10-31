namespace Colony.Generators.Tests;

/// <summary>What counts as System code and how a site opts out (ticket AC #1 and #3).</summary>
public sealed class SystemScopeTests
{
    [Fact]
    public void Every_member_of_a_System_type_is_System_code_including_lambdas_and_local_functions()
    {
        AnalyzerHarness.Verify("""
            using System;
            [Colony.System]
            struct GrowthSystem
            {
                private readonly double _rate = {|COL1001:Math.Exp|}(1.0);
                public GrowthSystem() { }
                public double Run(double x)
                {
                    Func<double, double> f = v => {|COL1001:Math.Log|}(v);
                    double Local(double v) => {|COL1001:Math.Pow|}(v, 2);
                    return f(x) + Local(x) + _rate;
                }
            }
            """);
    }

    [Fact]
    public void A_nested_type_inside_a_System_type_is_System_code()
    {
        AnalyzerHarness.Verify("""
            using System;
            [Colony.System]
            static class Outer
            {
                static class Inner
                {
                    public static double Run(double x) => {|COL1001:Math.Sin|}(x);
                }
            }
            """);
    }

    [Fact]
    public void AllowNonDeterministic_on_a_method_exempts_that_method_only()
    {
        AnalyzerHarness.Verify("""
            using System;
            [Colony.System]
            static class Pathing
            {
                [Colony.AllowNonDeterministic("debug-only estimate, never written to a Component")]
                public static double Estimate(double x) => Math.Sin(x);

                public static double Step(double x) => {|COL1001:Math.Sin|}(x);
            }
            """);
    }

    [Fact]
    public void AllowNonDeterministic_on_a_type_exempts_the_whole_type()
    {
        AnalyzerHarness.Verify("""
            using System;
            [Colony.System]
            [Colony.AllowNonDeterministic("legacy; tracked in TASK-99")]
            static class Legacy
            {
                public static double Step(double x) => Math.Sin(x);
            }
            """);
    }

    [Fact]
    public void A_pragma_suppresses_a_single_rule_at_a_single_site()
    {
        AnalyzerHarness.Verify("""
            using System;
            static class Pathing
            {
                [Colony.System]
                public static double Step(double x)
                {
            #pragma warning disable COL1001 // host-side estimate
                    double a = Math.Sin(x);
            #pragma warning restore COL1001
                    return a + {|COL1001:Math.Cos|}(x);
                }
            }
            """);
    }

    [Fact]
    public void The_real_Core_attributes_are_the_ones_the_analyzer_keys_on()
    {
        AnalyzerHarness.VerifyAgainstCore("""
            using System;
            using Colony;
            static class Pathing
            {
                [Colony.System]
                public static double Step(double x) => {|COL1001:Math.Sin|}(x);

                [Colony.System]
                [AllowNonDeterministic("host-side estimate")]
                public static double Estimate(double x) => Math.Sin(x);
            }
            """);
    }

    [Fact]
    public void Deterministic_Math_members_are_allowed_in_System_code()
    {
        AnalyzerHarness.Verify("""
            using System;
            static class Pathing
            {
                [Colony.System]
                public static int Step(int x, double d) => Math.Abs(x) + Math.Max(x, 1) + Math.Sign(d) + (int)Math.Floor(d) + Math.Clamp(x, 0, 9);
            }
            """);
    }
}
