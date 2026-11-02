using System.Runtime.CompilerServices;
using Colony.Numerics;

namespace Colony.ProcGen;

public static partial class Noise
{
    /// <summary>2D fractal Brownian motion over <see cref="FbmSettings.Basis"/>; octave <c>o</c> uses <c>key.Derive(o)</c>.</summary>
    public static Fixed32 Fbm(NoiseKey key, in FbmSettings settings, Fixed32 x, Fixed32 y)
    {
        settings.Validate();
        var plan = new FbmPlan(key, settings);
        var r = settings.Basis switch
        {
            NoiseBasis.Value => plan.Evaluate2<ValueOp, Lanes1>(Lanes1.Broadcast(x.Raw), Lanes1.Broadcast(y.Raw)),
            NoiseBasis.Simplex => plan.Evaluate2<SimplexOp, Lanes1>(Lanes1.Broadcast(x.Raw), Lanes1.Broadcast(y.Raw)),
            _ => plan.Evaluate2<GradientOp, Lanes1>(Lanes1.Broadcast(x.Raw), Lanes1.Broadcast(y.Raw)),
        };
        return Fixed32.FromRaw(Lanes1.Lane(r, 0));
    }

    /// <summary>3D fractal Brownian motion.</summary>
    public static Fixed32 Fbm(NoiseKey key, in FbmSettings settings, Fixed32 x, Fixed32 y, Fixed32 z)
    {
        settings.Validate();
        var plan = new FbmPlan(key, settings);
        var r = settings.Basis switch
        {
            NoiseBasis.Value => plan.Evaluate3<ValueOp, Lanes1>(Lanes1.Broadcast(x.Raw), Lanes1.Broadcast(y.Raw), Lanes1.Broadcast(z.Raw)),
            NoiseBasis.Simplex => plan.Evaluate3<SimplexOp, Lanes1>(Lanes1.Broadcast(x.Raw), Lanes1.Broadcast(y.Raw), Lanes1.Broadcast(z.Raw)),
            _ => plan.Evaluate3<GradientOp, Lanes1>(Lanes1.Broadcast(x.Raw), Lanes1.Broadcast(y.Raw), Lanes1.Broadcast(z.Raw)),
        };
        return Fixed32.FromRaw(Lanes1.Lane(r, 0));
    }

    /// <summary>2D fBm for a batch of positions.</summary>
    public static void Fbm(NoiseKey key, in FbmSettings settings, ReadOnlySpan<Fixed32> x, ReadOnlySpan<Fixed32> y, Span<Fixed32> result) => Fbm(key, settings, x, y, result, ActivePath);

    /// <summary>3D fBm for a batch of positions.</summary>
    public static void Fbm(NoiseKey key, in FbmSettings settings, ReadOnlySpan<Fixed32> x, ReadOnlySpan<Fixed32> y, ReadOnlySpan<Fixed32> z, Span<Fixed32> result) => Fbm(key, settings, x, y, z, result, ActivePath);

    internal static void Fbm(NoiseKey key, in FbmSettings settings, ReadOnlySpan<Fixed32> x, ReadOnlySpan<Fixed32> y, Span<Fixed32> result, KernelPath path)
    {
        settings.Validate();
        if (x.Length != y.Length || x.Length != result.Length)
        {
            ThrowLengthMismatch();
        }

        var plan = new FbmPlan(key, settings);
        switch (settings.Basis)
        {
            case NoiseBasis.Value:
                FbmBatch2<ValueOp>(in plan, Raw(x), Raw(y), Raw(result), path);
                break;
            case NoiseBasis.Simplex:
                FbmBatch2<SimplexOp>(in plan, Raw(x), Raw(y), Raw(result), path);
                break;
            default:
                FbmBatch2<GradientOp>(in plan, Raw(x), Raw(y), Raw(result), path);
                break;
        }
    }

    internal static void Fbm(NoiseKey key, in FbmSettings settings, ReadOnlySpan<Fixed32> x, ReadOnlySpan<Fixed32> y, ReadOnlySpan<Fixed32> z, Span<Fixed32> result, KernelPath path)
    {
        settings.Validate();
        if (x.Length != y.Length || x.Length != z.Length || x.Length != result.Length)
        {
            ThrowLengthMismatch();
        }

        var plan = new FbmPlan(key, settings);
        switch (settings.Basis)
        {
            case NoiseBasis.Value:
                FbmBatch3<ValueOp>(in plan, Raw(x), Raw(y), Raw(z), Raw(result), path);
                break;
            case NoiseBasis.Simplex:
                FbmBatch3<SimplexOp>(in plan, Raw(x), Raw(y), Raw(z), Raw(result), path);
                break;
            default:
                FbmBatch3<GradientOp>(in plan, Raw(x), Raw(y), Raw(z), Raw(result), path);
                break;
        }
    }

    private static void FbmBatch2<TOp>(in FbmPlan plan, ReadOnlySpan<int> x, ReadOnlySpan<int> y, Span<int> result, KernelPath path)
        where TOp : struct, INoiseOp
    {
        int i = path switch
        {
            KernelPath.Vector256 => RunFbm2<TOp, Lanes256>(in plan, x, y, result),
            KernelPath.Vector128 => RunFbm2<TOp, Lanes128>(in plan, x, y, result),
            _ => 0,
        };

        RunFbm2<TOp, Lanes1>(in plan, x[i..], y[i..], result[i..]);
    }

    private static void FbmBatch3<TOp>(in FbmPlan plan, ReadOnlySpan<int> x, ReadOnlySpan<int> y, ReadOnlySpan<int> z, Span<int> result, KernelPath path)
        where TOp : struct, INoiseOp
    {
        int i = path switch
        {
            KernelPath.Vector256 => RunFbm3<TOp, Lanes256>(in plan, x, y, z, result),
            KernelPath.Vector128 => RunFbm3<TOp, Lanes128>(in plan, x, y, z, result),
            _ => 0,
        };

        RunFbm3<TOp, Lanes1>(in plan, x[i..], y[i..], z[i..], result[i..]);
    }

    private static int RunFbm2<TOp, T>(in FbmPlan plan, ReadOnlySpan<int> x, ReadOnlySpan<int> y, Span<int> result)
        where TOp : struct, INoiseOp
        where T : struct, IIntLanes<T>
    {
        int count = T.Count;
        int i = 0;
        for (; i + count <= x.Length; i += count)
        {
            T.Store(plan.Evaluate2<TOp, T>(T.Load(x[i..]), T.Load(y[i..])), result[i..]);
        }

        return i;
    }

    private static int RunFbm3<TOp, T>(in FbmPlan plan, ReadOnlySpan<int> x, ReadOnlySpan<int> y, ReadOnlySpan<int> z, Span<int> result)
        where TOp : struct, INoiseOp
        where T : struct, IIntLanes<T>
    {
        int count = T.Count;
        int i = 0;
        for (; i + count <= x.Length; i += count)
        {
            T.Store(plan.Evaluate3<TOp, T>(T.Load(x[i..]), T.Load(y[i..]), T.Load(z[i..])), result[i..]);
        }

        return i;
    }

    /// <summary>
    /// The per-octave constants of one fBm evaluation, computed once per call (not per lane group): the derived key
    /// words, Q16 frequency and amplitude of each octave and the reciprocal of the total amplitude. Lives on the stack.
    /// </summary>
    private readonly struct FbmPlan
    {
        private readonly OctaveWords _keyLow;
        private readonly OctaveWords _keyHigh;
        private readonly OctaveWords _frequency;
        private readonly OctaveWords _amplitude;
        private readonly int _octaves;
        private readonly int _inverseTotalAmplitude;

        public FbmPlan(NoiseKey key, in FbmSettings settings)
        {
            _octaves = settings.Octaves;
            var amplitude = Fixed32.One;
            var frequency = Fixed32.One;
            var total = Fixed32.Zero;
            for (int o = 0; o < _octaves; o++)
            {
                var octaveKey = key.Derive((ulong)o);
                _keyLow[o] = (int)octaveKey.MixLow;
                _keyHigh[o] = (int)octaveKey.MixHigh;
                _frequency[o] = frequency.Raw;
                _amplitude[o] = amplitude.Raw;
                total += amplitude;
                amplitude *= settings.Gain;
                frequency *= settings.Lacunarity;
            }

            _inverseTotalAmplitude = (Fixed32.One / total).Raw;
        }

        public T Evaluate2<TOp, T>(T x, T y)
            where TOp : struct, INoiseOp
            where T : struct, IIntLanes<T>
        {
            var sum = T.Zero;
            for (int o = 0; o < _octaves; o++)
            {
                var frequency = T.Broadcast(_frequency[o]);
                var n = TOp.Evaluate2(T.Broadcast(_keyLow[o]), T.Broadcast(_keyHigh[o]), T.MultiplyQ16(x, frequency), T.MultiplyQ16(y, frequency));
                sum = T.Add(sum, T.MultiplyQ16(n, T.Broadcast(_amplitude[o])));
            }

            return NoiseKernels.ClampUnit(T.MultiplyQ16(sum, T.Broadcast(_inverseTotalAmplitude)));
        }

        public T Evaluate3<TOp, T>(T x, T y, T z)
            where TOp : struct, INoiseOp
            where T : struct, IIntLanes<T>
        {
            var sum = T.Zero;
            for (int o = 0; o < _octaves; o++)
            {
                var frequency = T.Broadcast(_frequency[o]);
                var n = TOp.Evaluate3(T.Broadcast(_keyLow[o]), T.Broadcast(_keyHigh[o]), T.MultiplyQ16(x, frequency), T.MultiplyQ16(y, frequency), T.MultiplyQ16(z, frequency));
                sum = T.Add(sum, T.MultiplyQ16(n, T.Broadcast(_amplitude[o])));
            }

            return NoiseKernels.ClampUnit(T.MultiplyQ16(sum, T.Broadcast(_inverseTotalAmplitude)));
        }

        [InlineArray(FbmSettings.MaxOctaves)]
        private struct OctaveWords
        {
            private int _element0;
        }
    }
}
