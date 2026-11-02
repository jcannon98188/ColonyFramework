using System.Runtime.Intrinsics;
using Colony.Numerics;

namespace Colony.Core.Tests.Numerics;

/// <summary>
/// The float layer is written once against a lane abstraction and instantiated for Vector256 (wide),
/// two Vector128 halves (fallback) and plain scalars. These tests prove the three paths produce the same bits
/// on the same inputs, including NaN, infinities, signed zero, denormals and tails that are not a multiple of
/// the logical width.
/// </summary>
[Collection(Colony.TestSupport.AllocationSensitiveGroup.Name)]
public sealed class FloatMathTests
{
    private static readonly SimdPath[] AllPaths = [SimdPath.Scalar, SimdPath.Vector128, SimdPath.Vector256];

    private static float[] Inputs(int count, int salt, float scale)
    {
        // Deterministic pseudo-random inputs with a sprinkling of special values; tests only, so System.Random is fine.
        var random = new Random(1234 + salt);
        var values = new float[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = (float)((random.NextDouble() * 2 - 1) * scale);
        }

        if (count > 16)
        {
            values[1] = float.NaN;
            values[2] = float.PositiveInfinity;
            values[3] = float.NegativeInfinity;
            values[4] = -0.0f;
            values[5] = 0.0f;
            values[6] = float.Epsilon;
            values[7] = -float.Epsilon;
            values[8] = float.MaxValue;
            values[9] = float.MinValue;
            values[10] = 1e-40f;
            values[11] = 16777217f;
        }

        return values;
    }

    private static void AssertSameBits(float[] expected, float[] actual, string what)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            int e = BitConverter.SingleToInt32Bits(expected[i]);
            int a = BitConverter.SingleToInt32Bits(actual[i]);
            Assert.True(e == a, $"{what}[{i}]: expected 0x{e:X8} ({expected[i]}) got 0x{a:X8} ({actual[i]})");
        }
    }

    [Fact]
    public void Logical_width_is_eight_lanes_and_the_active_path_is_a_real_vector_path()
    {
        Assert.Equal(8, FloatMath.LaneCount);
        Assert.Equal(Vector256.IsHardwareAccelerated ? SimdPath.Vector256 : SimdPath.Vector128, FloatMath.ActivePath);
    }

    [Theory]
    [InlineData(1003)]
    [InlineData(8)]
    [InlineData(7)]
    [InlineData(0)]
    public void Binary_operations_are_bit_identical_across_paths(int count)
    {
        var a = Inputs(count, 1, 1000f);
        var b = Inputs(count, 2, 1000f);
        var c = Inputs(count, 3, 1000f);
        var reference = new float[count];
        var actual = new float[count];

        foreach (var (name, op) in BinaryOperations())
        {
            op(SimdPath.Scalar, a, b, c, reference);
            foreach (var path in AllPaths)
            {
                op(path, a, b, c, actual);
                AssertSameBits(reference, actual, $"{name}/{path}");
            }
        }
    }

    private static IEnumerable<(string, Action<SimdPath, float[], float[], float[], float[]>)> BinaryOperations()
    {
        yield return ("Add", (p, a, b, _, r) => FloatMath.Add(a, b, r, p));
        yield return ("Subtract", (p, a, b, _, r) => FloatMath.Subtract(a, b, r, p));
        yield return ("Multiply", (p, a, b, _, r) => FloatMath.Multiply(a, b, r, p));
        yield return ("Divide", (p, a, b, _, r) => FloatMath.Divide(a, b, r, p));
        yield return ("Min", (p, a, b, _, r) => FloatMath.Min(a, b, r, p));
        yield return ("Max", (p, a, b, _, r) => FloatMath.Max(a, b, r, p));
        yield return ("MultiplyAdd", (p, a, b, c, r) => FloatMath.MultiplyAdd(a, b, c, r, p));
        yield return ("Scale", (p, a, b, _, r) => FloatMath.Scale(a, b.Length > 0 ? b[0] : 0.5f, r, p));
    }

    [Theory]
    [InlineData(1003, 100f)]
    [InlineData(1003, 1e30f)]
    [InlineData(13, 1f)]
    public void Unary_operations_are_bit_identical_across_paths(int count, float scale)
    {
        var a = Inputs(count, 4, scale);
        var reference = new float[count];
        var actual = new float[count];

        foreach (var (name, op) in UnaryOperations())
        {
            op(SimdPath.Scalar, a, reference);
            foreach (var path in AllPaths)
            {
                op(path, a, actual);
                AssertSameBits(reference, actual, $"{name}/{path}");
            }
        }
    }

    private static IEnumerable<(string, Action<SimdPath, float[], float[]>)> UnaryOperations()
    {
        yield return ("Sqrt", (p, a, r) => FloatMath.Sqrt(a, r, p));
        yield return ("Abs", (p, a, r) => FloatMath.Abs(a, r, p));
        yield return ("Sin", (p, a, r) => FloatMath.Sin(a, r, p));
        yield return ("Cos", (p, a, r) => FloatMath.Cos(a, r, p));
        yield return ("Exp", (p, a, r) => FloatMath.Exp(a, r, p));
        yield return ("Log", (p, a, r) => FloatMath.Log(a, r, p));
    }

    [Fact]
    public void Scalar_functions_match_the_span_functions_bit_for_bit()
    {
        var a = Inputs(257, 5, 50f);
        var sin = new float[a.Length];
        var cos = new float[a.Length];
        var exp = new float[a.Length];
        var log = new float[a.Length];
        var sqrt = new float[a.Length];
        FloatMath.Sin(a, sin);
        FloatMath.Cos(a, cos);
        FloatMath.Exp(a, exp);
        FloatMath.Log(a, log);
        FloatMath.Sqrt(a, sqrt);
        for (int i = 0; i < a.Length; i++)
        {
            Assert.Equal(BitConverter.SingleToInt32Bits(sin[i]), BitConverter.SingleToInt32Bits(FloatMath.Sin(a[i])));
            Assert.Equal(BitConverter.SingleToInt32Bits(cos[i]), BitConverter.SingleToInt32Bits(FloatMath.Cos(a[i])));
            Assert.Equal(BitConverter.SingleToInt32Bits(exp[i]), BitConverter.SingleToInt32Bits(FloatMath.Exp(a[i])));
            Assert.Equal(BitConverter.SingleToInt32Bits(log[i]), BitConverter.SingleToInt32Bits(FloatMath.Log(a[i])));
            Assert.Equal(BitConverter.SingleToInt32Bits(sqrt[i]), BitConverter.SingleToInt32Bits(FloatMath.Sqrt(a[i])));
        }
    }

    [Fact]
    public void Float_to_int_saturation_is_defined_and_identical_across_paths()
    {
        float[] inputs = [0f, 1.9f, -1.9f, 2147483520f, 2147483648f, -2147483648f, -2147483904f, 1e10f, -1e10f, float.PositiveInfinity, float.NegativeInfinity, float.NaN, -0.0f, 0.99999994f];
        int[] expected = [0, 1, -1, 2147483520, int.MaxValue, int.MinValue, int.MinValue, int.MaxValue, int.MinValue, int.MaxValue, int.MinValue, 0, 0, 0];

        for (int i = 0; i < inputs.Length; i++)
        {
            Assert.Equal(expected[i], FloatMath.ToInt32Saturating(inputs[i]));
        }

        var result = new int[inputs.Length];
        foreach (var path in AllPaths)
        {
            FloatMath.ToInt32Saturating(inputs, result, path);
            Assert.Equal(expected, result);
        }
    }

    [Fact]
    public void Sum_and_Dot_use_a_fixed_tree_identical_across_paths_and_lengths()
    {
        foreach (int count in new[] { 0, 1, 7, 8, 9, 16, 1003 })
        {
            var a = Inputs(count, 6, 1e6f);
            var b = Inputs(count, 7, 1e3f);
            for (int i = 0; i < count && i < 12; i++)
            {
                // Keep reductions finite so that ordering differences would be visible.
                a[i] = i * 0.75f;
                b[i] = 1.5f - i;
            }

            float sum = FloatMath.Sum(a, SimdPath.Scalar);
            float dot = FloatMath.Dot(a, b, SimdPath.Scalar);
            foreach (var path in AllPaths)
            {
                Assert.Equal(BitConverter.SingleToInt32Bits(sum), BitConverter.SingleToInt32Bits(FloatMath.Sum(a, path)));
                Assert.Equal(BitConverter.SingleToInt32Bits(dot), BitConverter.SingleToInt32Bits(FloatMath.Dot(a, b, path)));
            }

            Assert.Equal(BitConverter.SingleToInt32Bits(sum), BitConverter.SingleToInt32Bits(FloatMath.Sum(a)));
            Assert.Equal(BitConverter.SingleToInt32Bits(dot), BitConverter.SingleToInt32Bits(FloatMath.Dot(a, b)));
        }
    }

    [Fact]
    public void Sum_follows_the_documented_eight_accumulator_tree_not_the_sequential_order()
    {
        // Sequential summation gives 1 (the 1e8 cancels and swallows the small terms differently);
        // the eight-lane tree keeps lane 0 = 1e8 - 1e8 = 0 and lane 1 = 1 + 1 = 2.
        float[] values = [1e8f, 1f, 0, 0, 0, 0, 0, 0, -1e8f, 1f, 0, 0, 0, 0, 0, 0];
        Assert.Equal(2f, FloatMath.Sum(values));

        float sequential = 0;
        foreach (var v in values)
        {
            sequential += v;
        }

        Assert.Equal(1f, sequential);

        // Committed golden bits for a fixed input (pins the reduction order for future refactors).
        var fixedInput = new float[37];
        for (int i = 0; i < fixedInput.Length; i++)
        {
            fixedInput[i] = (i % 2 == 0 ? 1 : -1) * (i * 1.1f + 0.3f);
        }

        Assert.Equal(0x41A0CCCC, BitConverter.SingleToInt32Bits(FloatMath.Sum(fixedInput))); // 20.1f
    }

    [Fact]
    public void MultiplyAdd_is_never_contracted_to_a_fused_multiply_add()
    {
        // (1 + 2^-20)^2 = 1 + 2^-19 + 2^-40. A fused multiply-add with c = -(1 + 2^-19) yields 2^-40;
        // separately rounded multiply then add yields exactly zero.
        float a = 1 + MathF.Pow(2, -20);
        float c = -(1 + MathF.Pow(2, -19));
        float[] av = Enumerable.Repeat(a, 9).ToArray();
        float[] cv = Enumerable.Repeat(c, 9).ToArray();
        var result = new float[9];
        foreach (var path in AllPaths)
        {
            FloatMath.MultiplyAdd(av, av, cv, result, path);
            Assert.All(result, r => Assert.Equal(0f, r));
        }

        Assert.Equal(0f, FloatMath.MultiplyAdd(a, a, c));
    }

    [Theory]
    [InlineData(0f, 100f, 2.5e-7)]
    [InlineData(0f, 10f, 2e-7)]
    [InlineData(1000f, 1200f, 1e-5)]
    public void Sin_and_Cos_are_accurate_against_double_precision(float min, float max, double tolerance)
    {
        for (int i = 0; i <= 2000; i++)
        {
            float x = min + (max - min) * i / 2000f;
            Assert.InRange(FloatMath.Sin(x) - Math.Sin(x), -tolerance, tolerance);
            Assert.InRange(FloatMath.Cos(x) - Math.Cos(x), -tolerance, tolerance);
            Assert.InRange(FloatMath.Sin(-x) - Math.Sin(-x), -tolerance, tolerance);
        }

        Assert.Equal(0f, FloatMath.Sin(0f));
        Assert.Equal(1f, FloatMath.Cos(0f));
        Assert.True(float.IsNaN(FloatMath.Sin(float.NaN)));
        Assert.True(float.IsNaN(FloatMath.Sin(float.PositiveInfinity)));
        Assert.True(float.IsNaN(FloatMath.Cos(float.NegativeInfinity)));
    }

    [Fact]
    public void Exp_is_accurate_and_handles_the_edges()
    {
        for (int i = 0; i <= 2000; i++)
        {
            float x = -87f + 174f * i / 2000f;
            double expected = Math.Exp(x);
            double relative = (FloatMath.Exp(x) - expected) / expected;
            Assert.InRange(relative, -3e-7, 3e-7);
        }

        Assert.Equal(1f, FloatMath.Exp(0f));
        Assert.Equal(float.PositiveInfinity, FloatMath.Exp(89f));
        Assert.Equal(float.PositiveInfinity, FloatMath.Exp(float.PositiveInfinity));
        Assert.Equal(0f, FloatMath.Exp(-104f));
        Assert.Equal(0f, FloatMath.Exp(float.NegativeInfinity));
        Assert.True(float.IsNaN(FloatMath.Exp(float.NaN)));
    }

    [Fact]
    public void Log_is_accurate_and_handles_the_edges()
    {
        for (int i = 0; i <= 2000; i++)
        {
            float x = MathF.Pow(10f, -30f + 60f * i / 2000f);
            double expected = Math.Log(x);
            double tolerance = 4e-7 * Math.Max(1, Math.Abs(expected));
            Assert.InRange(FloatMath.Log(x) - expected, -tolerance, tolerance);
        }

        Assert.Equal(0f, FloatMath.Log(1f));
        Assert.Equal(float.NegativeInfinity, FloatMath.Log(0f));
        Assert.Equal(float.NegativeInfinity, FloatMath.Log(-0f));
        Assert.True(float.IsNaN(FloatMath.Log(-1f)));
        Assert.True(float.IsNaN(FloatMath.Log(float.NaN)));
        Assert.Equal(float.PositiveInfinity, FloatMath.Log(float.PositiveInfinity));
        Assert.InRange(FloatMath.Log(1e-40f) - Math.Log(1e-40f), -1e-5, 1e-5); // denormal input
    }

    [Fact]
    public void Transcendentals_have_committed_golden_bits()
    {
        Assert.Equal(0x3F576AA4, BitConverter.SingleToInt32Bits(FloatMath.Sin(1f)));
        Assert.Equal(0x3F0A5140, BitConverter.SingleToInt32Bits(FloatMath.Cos(1f)));
        Assert.Equal(0x402DF854, BitConverter.SingleToInt32Bits(FloatMath.Exp(1f)));
        Assert.Equal(0x3F317218, BitConverter.SingleToInt32Bits(FloatMath.Log(2f)));
        Assert.Equal(0x3FB504F3, BitConverter.SingleToInt32Bits(FloatMath.Sqrt(2f)));
    }

    [Fact]
    public void Span_operations_do_not_allocate()
    {
        var a = Inputs(1003, 8, 10f);
        var b = Inputs(1003, 9, 10f);
        var r = new float[1003];
        var ints = new int[1003];
        Run();

        long before = GC.GetAllocatedBytesForCurrentThread();
        Run();
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);

        void Run()
        {
            FloatMath.Add(a, b, r);
            FloatMath.MultiplyAdd(a, b, r, r);
            FloatMath.Sin(a, r);
            FloatMath.Exp(a, r);
            FloatMath.Log(a, r);
            FloatMath.Sqrt(a, r);
            FloatMath.ToInt32Saturating(a, ints);
            _ = FloatMath.Sum(a) + FloatMath.Dot(a, b) + FloatMath.Sin(1f) + FloatMath.Exp(1f);
        }
    }

    [Fact]
    public void Mismatched_span_lengths_are_rejected()
    {
        var a = new float[8];
        var b = new float[7];
        Assert.Throws<ArgumentException>(() => FloatMath.Add(a, b, a));
        Assert.Throws<ArgumentException>(() => FloatMath.Sin(a, b));
        Assert.Throws<ArgumentException>(() => FloatMath.Dot(a, b));
        Assert.Throws<ArgumentException>(() => FloatMath.ToInt32Saturating(a, new int[3]));
    }
}
