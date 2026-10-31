namespace Colony.Generators.Tests;

/// <summary>COL1001: Math/MathF transcendentals and other non-deterministic math members (spec section 6).</summary>
public sealed class MathRuleTests
{
    [Fact]
    public void Math_transcendental_inside_a_System_method_is_reported()
    {
        AnalyzerHarness.Verify("""
            using System;
            static class Movement
            {
                [Colony.System]
                public static float Step(float x) => (float){|COL1001:Math.Sin|}(x) + {|COL1001:MathF.Exp|}(x);
            }
            """);
    }

    [Fact]
    public void Math_transcendental_outside_System_code_is_not_reported()
    {
        AnalyzerHarness.Verify("""
            using System;
            static class Rendering
            {
                public static float Step(float x) => (float)Math.Sin(x) + MathF.Exp(x);
            }
            """);
    }
}
