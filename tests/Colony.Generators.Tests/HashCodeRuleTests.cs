namespace Colony.Generators.Tests;

/// <summary>COL1005: GetHashCode on reference types (identity hashes and randomised string hashing; spec section 6).</summary>
public sealed class HashCodeRuleTests
{
    [Fact]
    public void GetHashCode_on_reference_types_is_reported()
    {
        AnalyzerHarness.Verify("""
            using System;
            using System.Collections.Generic;
            using System.Runtime.CompilerServices;
            [Colony.System]
            sealed class Hasher
            {
                public int Of(object o, string s, int[] a) => {|COL1005:o.GetHashCode|}() + {|COL1005:s.GetHashCode|}() + {|COL1005:a.GetHashCode|}();
                public int OfThis() => {|COL1005:GetHashCode|}();
                public int OfBase() => {|COL1005:base.GetHashCode|}();
                public int Generic<T>(T value) => {|COL1005:value.GetHashCode|}();
                public int Identity(object o) => {|COL1005:RuntimeHelpers.GetHashCode|}(o);
                public int Comparer(string s) => {|COL1005:EqualityComparer<string>.Default.GetHashCode|}(s);
                public int Span(ReadOnlySpan<char> s) => {|COL1005:string.GetHashCode|}(s);
                public int Combined(string s, int i) => {|COL1005:HashCode.Combine|}(s, i);
            }
            """);
    }

    [Fact]
    public void GetHashCode_on_value_types_is_allowed()
    {
        AnalyzerHarness.Verify("""
            using System;
            readonly struct EntityId { public readonly long Value; public override int GetHashCode() => Value.GetHashCode(); }
            [Colony.System]
            sealed class Hasher
            {
                public int Of(int i, EntityId id, Guid g) => i.GetHashCode() + id.GetHashCode() + g.GetHashCode();
                public int Generic<T>(T value) where T : struct => value.GetHashCode();
                public int Combined(int i, EntityId id) => HashCode.Combine(i, id);
            }
            """);
    }

    [Fact]
    public void GetHashCode_on_reference_types_outside_System_code_is_allowed()
    {
        AnalyzerHarness.Verify("""
            sealed class Host
            {
                public int Of(object o, string s) => o.GetHashCode() + s.GetHashCode();
            }
            """);
    }
}
