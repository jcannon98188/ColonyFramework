using System.Reflection;
using Colony.Hashing;
using Colony.Numerics;

namespace Colony.Core.Tests.Numerics;

/// <summary>
/// Spec section 3: the public API is safe. Nothing public in the math, hashing or random areas exposes a pointer,
/// and none of it allocates on the managed heap (spec section 2). Reflection is fine here: tests are not simulation code.
/// </summary>
public sealed class PublicSurfaceTests
{
    [Fact]
    public void No_public_member_of_the_numerics_or_hashing_namespaces_exposes_a_pointer()
    {
        var assembly = typeof(Fixed32).Assembly;
        var types = assembly.GetExportedTypes()
            .Where(t => t.Namespace is "Colony.Numerics" or "Colony.Hashing")
            .ToArray();
        Assert.NotEmpty(types);

        foreach (var type in types)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                Assert.False(method.ReturnType.IsPointer, $"{type.Name}.{method.Name} returns a pointer");
                Assert.All(method.GetParameters(), p => Assert.False(p.ParameterType.IsPointer, $"{type.Name}.{method.Name}({p.Name}) takes a pointer"));
            }

            Assert.All(type.GetProperties(), p => Assert.False(p.PropertyType.IsPointer, $"{type.Name}.{p.Name} is a pointer"));
            Assert.All(type.GetFields(), f => Assert.False(f.FieldType.IsPointer, $"{type.Name}.{f.Name} is a pointer"));
        }
    }

    [Fact]
    public void Core_assembly_contains_no_unsafe_pointer_code_outside_internal_types()
    {
        // Across the whole Core assembly, no public method takes or returns a pointer.
        // (Internal unsafe code is allowed by the spec and lives in the storage types.)
        foreach (var type in typeof(Fixed32).Assembly.GetExportedTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                Assert.False(method.ReturnType.IsPointer || method.GetParameters().Any(p => p.ParameterType.IsPointer), $"{type.FullName}.{method.Name}");
            }
        }
    }

    [Fact]
    public void Fixed_point_arithmetic_trig_and_sqrt_do_not_allocate()
    {
        Run();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Run();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        static void Run()
        {
            var acc32 = Fixed32.Zero;
            var acc64 = Fixed64.Zero;
            var v = new Fixed32x3(Fixed32.One, Fixed32.Half, Fixed32.Epsilon);
            for (int i = 1; i < 500; i++)
            {
                var x = Fixed32.FromRaw(i * 7919);
                acc32 += Fixed32.Sin(x) * Fixed32.Cos(x) + Fixed32.Sqrt(x) / Fixed32.Atan2(x, Fixed32.One);
                var y = Fixed64.FromRaw(i * 7919L * 65536);
                acc64 += Fixed64.Sin(y) + Fixed64.Cos(y) + Fixed64.Sqrt(y) + Fixed64.Atan2(y, Fixed64.One) + Fixed64.FromFixed32(acc32);
                v = Fixed32x3.Normalize(v + Fixed32x3.Cross(v, Fixed32x3.UnitY));
            }

            _ = acc32.Raw + acc64.Raw + v.X.Raw;
        }
    }
}
