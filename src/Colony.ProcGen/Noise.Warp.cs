using Colony.Numerics;

namespace Colony.ProcGen;

public static partial class Noise
{
    /// <summary>
    /// Domain warping: offsets a 2D position by <paramref name="strength"/> times gradient noise, with an independent
    /// key per axis (<c>key.Derive(0)</c> for x, <c>key.Derive(1)</c> for y). Feed the result into any other noise.
    /// </summary>
    public static (Fixed32 X, Fixed32 Y) Warp(NoiseKey key, Fixed32 strength, Fixed32 x, Fixed32 y)
    {
        var plan = new WarpPlan(key, strength);
        var px = Lanes1.Broadcast(x.Raw);
        var py = Lanes1.Broadcast(y.Raw);
        plan.Warp2(ref px, ref py);
        return (Fixed32.FromRaw(Lanes1.Lane(px, 0)), Fixed32.FromRaw(Lanes1.Lane(py, 0)));
    }

    /// <summary>3D domain warping; axis z uses <c>key.Derive(2)</c>.</summary>
    public static (Fixed32 X, Fixed32 Y, Fixed32 Z) Warp(NoiseKey key, Fixed32 strength, Fixed32 x, Fixed32 y, Fixed32 z)
    {
        var plan = new WarpPlan(key, strength);
        var px = Lanes1.Broadcast(x.Raw);
        var py = Lanes1.Broadcast(y.Raw);
        var pz = Lanes1.Broadcast(z.Raw);
        plan.Warp3(ref px, ref py, ref pz);
        return (Fixed32.FromRaw(Lanes1.Lane(px, 0)), Fixed32.FromRaw(Lanes1.Lane(py, 0)), Fixed32.FromRaw(Lanes1.Lane(pz, 0)));
    }

    /// <summary>Warps a batch of 2D positions in place.</summary>
    public static void Warp(NoiseKey key, Fixed32 strength, Span<Fixed32> x, Span<Fixed32> y) => Warp(key, strength, x, y, ActivePath);

    /// <summary>Warps a batch of 3D positions in place.</summary>
    public static void Warp(NoiseKey key, Fixed32 strength, Span<Fixed32> x, Span<Fixed32> y, Span<Fixed32> z) => Warp(key, strength, x, y, z, ActivePath);

    internal static void Warp(NoiseKey key, Fixed32 strength, Span<Fixed32> x, Span<Fixed32> y, KernelPath path)
    {
        if (x.Length != y.Length)
        {
            ThrowLengthMismatch();
        }

        var plan = new WarpPlan(key, strength);
        var xs = Raw(x);
        var ys = Raw(y);
        int i = path switch
        {
            KernelPath.Vector256 => RunWarp2<Lanes256>(in plan, xs, ys),
            KernelPath.Vector128 => RunWarp2<Lanes128>(in plan, xs, ys),
            _ => 0,
        };

        RunWarp2<Lanes1>(in plan, xs[i..], ys[i..]);
    }

    internal static void Warp(NoiseKey key, Fixed32 strength, Span<Fixed32> x, Span<Fixed32> y, Span<Fixed32> z, KernelPath path)
    {
        if (x.Length != y.Length || x.Length != z.Length)
        {
            ThrowLengthMismatch();
        }

        var plan = new WarpPlan(key, strength);
        var xs = Raw(x);
        var ys = Raw(y);
        var zs = Raw(z);
        int i = path switch
        {
            KernelPath.Vector256 => RunWarp3<Lanes256>(in plan, xs, ys, zs),
            KernelPath.Vector128 => RunWarp3<Lanes128>(in plan, xs, ys, zs),
            _ => 0,
        };

        RunWarp3<Lanes1>(in plan, xs[i..], ys[i..], zs[i..]);
    }

    private static int RunWarp2<T>(in WarpPlan plan, Span<int> x, Span<int> y)
        where T : struct, IIntLanes<T>
    {
        int count = T.Count;
        int i = 0;
        for (; i + count <= x.Length; i += count)
        {
            var px = T.Load(x[i..]);
            var py = T.Load(y[i..]);
            plan.Warp2(ref px, ref py);
            T.Store(px, x[i..]);
            T.Store(py, y[i..]);
        }

        return i;
    }

    private static int RunWarp3<T>(in WarpPlan plan, Span<int> x, Span<int> y, Span<int> z)
        where T : struct, IIntLanes<T>
    {
        int count = T.Count;
        int i = 0;
        for (; i + count <= x.Length; i += count)
        {
            var px = T.Load(x[i..]);
            var py = T.Load(y[i..]);
            var pz = T.Load(z[i..]);
            plan.Warp3(ref px, ref py, ref pz);
            T.Store(px, x[i..]);
            T.Store(py, y[i..]);
            T.Store(pz, z[i..]);
        }

        return i;
    }

    private readonly struct WarpPlan
    {
        private readonly NoiseKey _xKey;
        private readonly NoiseKey _yKey;
        private readonly NoiseKey _zKey;
        private readonly int _strength;

        public WarpPlan(NoiseKey key, Fixed32 strength)
        {
            _xKey = key.Derive(0);
            _yKey = key.Derive(1);
            _zKey = key.Derive(2);
            _strength = strength.Raw;
        }

        public void Warp2<T>(ref T x, ref T y)
            where T : struct, IIntLanes<T>
        {
            var strength = T.Broadcast(_strength);
            var dx = T.MultiplyQ16(strength, NoiseKernels.Gradient2(T.Broadcast((int)_xKey.MixLow), T.Broadcast((int)_xKey.MixHigh), x, y));
            var dy = T.MultiplyQ16(strength, NoiseKernels.Gradient2(T.Broadcast((int)_yKey.MixLow), T.Broadcast((int)_yKey.MixHigh), x, y));
            x = T.Add(x, dx);
            y = T.Add(y, dy);
        }

        public void Warp3<T>(ref T x, ref T y, ref T z)
            where T : struct, IIntLanes<T>
        {
            var strength = T.Broadcast(_strength);
            var dx = T.MultiplyQ16(strength, NoiseKernels.Gradient3(T.Broadcast((int)_xKey.MixLow), T.Broadcast((int)_xKey.MixHigh), x, y, z));
            var dy = T.MultiplyQ16(strength, NoiseKernels.Gradient3(T.Broadcast((int)_yKey.MixLow), T.Broadcast((int)_yKey.MixHigh), x, y, z));
            var dz = T.MultiplyQ16(strength, NoiseKernels.Gradient3(T.Broadcast((int)_zKey.MixLow), T.Broadcast((int)_zKey.MixHigh), x, y, z));
            x = T.Add(x, dx);
            y = T.Add(y, dy);
            z = T.Add(z, dz);
        }
    }
}
