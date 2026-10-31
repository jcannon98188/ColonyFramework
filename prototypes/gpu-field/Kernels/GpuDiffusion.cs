using System.Runtime.InteropServices;
using GpuField.Backend;
using GpuField.Fields;

namespace GpuField.Kernels;

/// <summary>
/// GPU mirror of a <see cref="ChunkedField{T}"/> on the wgpu Backend: the same slot pool layout (so a full
/// upload is one memcpy), the chunk table, uniform values and a staging ring for dirty-chunk transfers.
/// </summary>
public sealed unsafe class GpuDiffusion<T, TRule> : IDisposable where T : unmanaged where TRule : struct, IStencilRule<T>
{
    public const int ChunkBytesInts = ChunkedField<T>.CellsPerChunk;
    public readonly WgpuDevice Dev;
    readonly ChunkedField<T> _f;
    public readonly WgpuBuffer PoolA, PoolB, SlotOf, UniformVal, Active, ChunkOfSlot, Params;
    readonly IKernel _kernel;
    readonly IBindingSet _ab, _ba;
    bool _curIsA = true;
    uint _activeCount;
    public readonly ulong ChunkBytes = (ulong)(ChunkedField<T>.CellsPerChunk * sizeof(T));

    // Staging ring: R buffers for upload and R for readback, each able to hold `ringChunks` packed chunks.
    readonly WgpuBuffer[] _up, _down;
    readonly bool[] _upMapped;
    int _upNext, _downNext;
    public readonly int RingChunks;

    // Timestamps: pairs of (begin,end) per compute pass.
    public readonly nint QuerySet;
    readonly WgpuBuffer _tsResolve, _tsRead;
    public const int MaxTimestampPairs = 64;

    public WgpuBuffer CurPool => _curIsA ? PoolA : PoolB;
    public long DeviceBytes { get; }

    public static string Shader => $$"""
        alias T = {{TRule.WgslType}};
        struct Params { activeCount: u32, groupsX: u32, cx: i32, cy: i32, cz: i32, p0: u32, p1: u32, p2: u32 };
        @group(0) @binding(0) var<storage, read> src: array<T>;
        @group(0) @binding(1) var<storage, read_write> dst: array<T>;
        @group(0) @binding(2) var<storage, read> slotOf: array<i32>;
        @group(0) @binding(3) var<storage, read> uniformVal: array<T>;
        @group(0) @binding(4) var<storage, read> activeSlots: array<u32>;
        @group(0) @binding(5) var<storage, read> chunkOfSlot: array<i32>;
        @group(0) @binding(6) var<uniform> P: Params;

        // Value of local cell `idx` in neighbour chunk `ncid`; -1 means outside the World (clamp to self).
        fn nb(ncid: i32, idx: u32, selfv: T) -> T {
            if (ncid < 0) { return selfv; }
            let s = slotOf[ncid];
            if (s < 0) { return uniformVal[ncid]; }
            return src[u32(s) * 4096u + idx];
        }

        // One workgroup per active chunk; one thread per (x,y) column, marching z with a rolling window.
        @compute @workgroup_size(16, 16, 1)
        fn main(@builtin(workgroup_id) wg: vec3<u32>, @builtin(local_invocation_id) lid: vec3<u32>) {
            let i = wg.y * P.groupsX + wg.x;
            if (i >= P.activeCount) { return; }
            let slot = activeSlots[i];
            let cid = chunkOfSlot[slot];
            let ccx = cid % P.cx;
            let ccy = (cid / P.cx) % P.cy;
            let ccz = cid / (P.cx * P.cy);
            let plane = P.cx * P.cy;
            let nxm = select(-1, cid - 1, ccx > 0);
            let nxp = select(-1, cid + 1, ccx < P.cx - 1);
            let nym = select(-1, cid - P.cx, ccy > 0);
            let nyp = select(-1, cid + P.cx, ccy < P.cy - 1);
            let nzm = select(-1, cid - plane, ccz > 0);
            let nzp = select(-1, cid + plane, ccz < P.cz - 1);
            let x = lid.x;
            let y = lid.y;
            let base = slot * 4096u;
            let col = y * 16u + x;
            var c: T = src[base + col];
            var zm: T = nb(nzm, 15u * 256u + col, c);
            for (var z = 0u; z < 16u; z++) {
                let idx = z * 256u + col;
                var zp: T;
                if (z < 15u) { zp = src[base + idx + 256u]; } else { zp = nb(nzp, col, c); }
                var xm: T;
                if (x > 0u) { xm = src[base + idx - 1u]; } else { xm = nb(nxm, idx + 15u, c); }
                var xp: T;
                if (x < 15u) { xp = src[base + idx + 1u]; } else { xp = nb(nxp, idx - 15u, c); }
                var ym: T;
                if (y > 0u) { ym = src[base + idx - 16u]; } else { ym = nb(nym, idx + 240u, c); }
                var yp: T;
                if (y < 15u) { yp = src[base + idx + 16u]; } else { yp = nb(nyp, idx - 240u, c); }
                dst[base + idx] = {{TRule.Wgsl}};
                zm = c;
                c = zp;
            }
        }
        """;

    public GpuDiffusion(WgpuDevice dev, ChunkedField<T> f, int ringChunks = 4096, int ringSize = 3)
    {
        Dev = dev; _f = f; RingChunks = ringChunks;
        ulong pool = (ulong)f.PoolBytes;
        PoolA = dev.CreateBufferRaw(pool, BufferKind.Storage, "poolA", false);
        PoolB = dev.CreateBufferRaw(pool, BufferKind.Storage, "poolB", false);
        SlotOf = dev.CreateBufferRaw((ulong)f.ChunkCount * 4, BufferKind.Storage, "slotOf", false);
        UniformVal = dev.CreateBufferRaw((ulong)f.ChunkCount * (ulong)sizeof(T), BufferKind.Storage, "uniformVal", false);
        Active = dev.CreateBufferRaw((ulong)Math.Max(1, f.SlotCount) * 4, BufferKind.Storage, "active", false);
        ChunkOfSlot = dev.CreateBufferRaw((ulong)Math.Max(1, f.SlotCount) * 4, BufferKind.Storage, "chunkOfSlot", false);
        Params = dev.CreateBufferRaw(32, BufferKind.Uniform, "params", false);

        fixed (int* p = f.SlotOf) dev.Write(SlotOf, 0, p, (nuint)f.ChunkCount * 4);
        fixed (T* p = f.UniformValue) dev.Write(UniformVal, 0, p, (nuint)(f.ChunkCount * sizeof(T)));
        fixed (int* p = f.ChunkOfSlot) dev.Write(ChunkOfSlot, 0, p, (nuint)f.SlotCount * 4);
        var all = new uint[f.SlotCount];
        for (int i = 0; i < all.Length; i++) all[i] = (uint)i;
        SetActive(all);

        BindingKind[] layout = [BindingKind.StorageRead, BindingKind.StorageReadWrite, BindingKind.StorageRead, BindingKind.StorageRead,
            BindingKind.StorageRead, BindingKind.StorageRead, BindingKind.Uniform];
        _kernel = dev.CreateKernel($"diffuse-{TRule.WgslType}", Shader, "main", layout);
        _ab = dev.CreateBindings(_kernel, [(PoolA, 0, 0), (PoolB, 0, 0), (SlotOf, 0, 0), (UniformVal, 0, 0), (Active, 0, 0), (ChunkOfSlot, 0, 0), (Params, 0, 0)]);
        _ba = dev.CreateBindings(_kernel, [(PoolB, 0, 0), (PoolA, 0, 0), (SlotOf, 0, 0), (UniformVal, 0, 0), (Active, 0, 0), (ChunkOfSlot, 0, 0), (Params, 0, 0)]);

        ulong ringBytes = (ulong)ringChunks * ChunkBytes;
        _up = new WgpuBuffer[ringSize]; _down = new WgpuBuffer[ringSize]; _upMapped = new bool[ringSize];
        for (int i = 0; i < ringSize; i++)
        {
            _up[i] = dev.CreateBufferRaw(ringBytes, BufferKind.UploadStaging, $"up{i}", mappedAtCreation: true);
            _upMapped[i] = true;
            _down[i] = dev.CreateBufferRaw(ringBytes, BufferKind.ReadbackStaging, $"down{i}", false);
        }

        if (dev.Capabilities.TimestampQuery)
        {
            QuerySet = dev.CreateTimestampQuerySet(MaxTimestampPairs * 2);
            _tsResolve = dev.CreateBufferRaw(MaxTimestampPairs * 16, BufferKind.Timestamps, "tsResolve", false);
            _tsRead = dev.CreateBufferRaw(MaxTimestampPairs * 16, BufferKind.ReadbackStaging, "tsRead", false);
        }
        else { _tsResolve = null!; _tsRead = null!; }

        DeviceBytes = (long)(2 * pool + (ulong)f.ChunkCount * (4ul + (ulong)sizeof(T)) + 2ul * (ulong)f.SlotCount * 4 + 32 + 2 * (ulong)ringSize * ringBytes);
        dev.WaitIdle();
    }

    public void SetActive(ReadOnlySpan<uint> slots)
    {
        _activeCount = (uint)slots.Length;
        fixed (uint* p = slots) if (slots.Length > 0) Dev.Write(Active, 0, p, (nuint)slots.Length * 4);
        uint gx = Math.Min(_activeCount, Dev.Capabilities.MaxWorkgroupsPerDimension);
        var prm = stackalloc uint[8];
        prm[0] = _activeCount; prm[1] = Math.Max(1, gx); prm[2] = (uint)_f.Cx; prm[3] = (uint)_f.Cy; prm[4] = (uint)_f.Cz;
        prm[5] = prm[6] = prm[7] = 0;
        Dev.Write(Params, 0, prm, 32);
    }

    public (uint gx, uint gy) Groups
    {
        get
        {
            uint gx = Math.Max(1, Math.Min(_activeCount, Dev.Capabilities.MaxWorkgroupsPerDimension));
            return (gx, (_activeCount + gx - 1) / gx);
        }
    }

    /// <summary>Records `steps` Kernel dispatches (ping-pong) into the batch. Optional timestamp pair index per step.</summary>
    public void RecordSteps(ICommandBatch batch, int steps, int firstTimestampPair = -1)
    {
        var (gx, gy) = Groups;
        ((WgpuBatch)batch).TimestampQuerySet = QuerySet;
        for (int i = 0; i < steps; i++)
        {
            batch.Dispatch(_kernel, _curIsA ? _ab : _ba, gx, gy, firstTimestampPair >= 0 ? firstTimestampPair + i : -1);
            _curIsA = !_curIsA;
        }
    }

    /// <summary>Resolves timestamp pairs [0,count) and returns per-pass GPU durations in ms (blocking).</summary>
    public double[] ReadTimestamps(int count)
    {
        var b = (WgpuBatch)Dev.BeginBatch();
        b.TimestampQuerySet = QuerySet;
        b.ResolveTimestamps(0, (uint)count * 2, _tsResolve, 0);
        b.Copy(_tsResolve, 0, _tsRead, 0, (ulong)count * 16);
        Dev.Submit(b).Wait();
        _tsRead.BeginMap(false, 0, (nuint)count * 16);
        _tsRead.FinishMap();
        var t = (ulong*)_tsRead.ConstMappedRange(0, (nuint)count * 16);
        var r = new double[count];
        for (int i = 0; i < count; i++) r[i] = (t[2 * i + 1] - t[2 * i]) * Dev.TimestampPeriodNs / 1e6;
        _tsRead.Unmap();
        return r;
    }

    // ---- transfers ----------------------------------------------------------------------------

    /// <summary>Full upload of the CPU pool (Cur) into the GPU's current pool with queue.writeBuffer.</summary>
    public void UploadAll() => Dev.Write(CurPool, 0, _f.Cur, (nuint)_f.PoolBytes);

    /// <summary>
    /// Dirty-chunk upload through the staging ring: pack chunks into a mapped MapWrite buffer, unmap, and record one
    /// copyBufferToBuffer per chunk into `batch`. Run-length merges adjacent slots. Returns the ring buffer used so the
    /// caller can re-map it once the batch's fence completes (<see cref="RecycleUpload"/>).
    /// </summary>
    public int UploadChunks(ICommandBatch batch, ReadOnlySpan<int> slots)
    {
        if (slots.Length > RingChunks) throw new ArgumentException("batch larger than ring buffer");
        int r = _upNext; _upNext = (_upNext + 1) % _up.Length;
        var buf = _up[r];
        if (!_upMapped[r]) { buf.FinishMap(); _upMapped[r] = true; }
        byte* dst = (byte*)buf.MappedRange(0, (nuint)(slots.Length * (long)ChunkBytes));
        for (int i = 0; i < slots.Length; i++)
            NativeMemory.Copy(_f.ChunkPtr(_f.Cur, slots[i]), dst + (long)i * (long)ChunkBytes, (nuint)ChunkBytes);
        buf.Unmap();
        _upMapped[r] = false;
        RecordRuns(slots, (i, s, n) => batch.Copy(buf, (ulong)i * ChunkBytes, CurPool, (ulong)s * ChunkBytes, (ulong)n * ChunkBytes));
        return r;
    }

    /// <summary>Starts re-mapping an upload ring buffer for writing. Call after the batch that used it was submitted.</summary>
    public void RecycleUpload(int ring) => _up[ring].BeginMap(true);

    /// <summary>Alternative dirty upload: one queue.writeBuffer per run of chunks (wgpu stages internally).</summary>
    public void UploadChunksWriteBuffer(ReadOnlySpan<int> slots)
    {
        var pool = CurPool;
        RecordRuns(slots, (i, s, n) => Dev.Write(pool, (ulong)s * ChunkBytes, _f.ChunkPtr(_f.Cur, s), (nuint)((ulong)n * ChunkBytes)));
    }

    /// <summary>Records copies of the given slots from the current GPU pool into a readback ring buffer. Returns the ring index.</summary>
    public int RecordReadback(ICommandBatch batch, ReadOnlySpan<int> slots)
    {
        if (slots.Length > RingChunks) throw new ArgumentException("batch larger than ring buffer");
        int r = _downNext; _downNext = (_downNext + 1) % _down.Length;
        var buf = _down[r];
        var pool = CurPool;
        RecordRuns(slots, (i, s, n) => batch.Copy(pool, (ulong)s * ChunkBytes, buf, (ulong)i * ChunkBytes, (ulong)n * ChunkBytes));
        return r;
    }

    public void BeginReadbackMap(int ring, int chunkCount) => _down[ring].BeginMap(false, 0, (nuint)((ulong)chunkCount * ChunkBytes));
    public bool ReadbackReady(int ring) => _down[ring].TryFinishMap();

    /// <summary>Finishes a readback (waits if the GPU is late) and scatters chunks into the CPU pool's Cur.</summary>
    public void CompleteReadback(int ring, ReadOnlySpan<int> slots)
    {
        var buf = _down[ring];
        buf.FinishMap();
        byte* src = (byte*)buf.ConstMappedRange(0, (nuint)((ulong)slots.Length * ChunkBytes));
        for (int i = 0; i < slots.Length; i++)
            NativeMemory.Copy(src + (long)i * (long)ChunkBytes, _f.ChunkPtr(_f.Cur, slots[i]), (nuint)ChunkBytes);
        buf.Unmap();
    }

    /// <summary>Blocking full readback of the current GPU pool into the CPU pool (chunked through the ring).</summary>
    public void ReadbackAll()
    {
        for (int start = 0; start < _f.SlotCount; start += RingChunks)
        {
            int n = Math.Min(RingChunks, _f.SlotCount - start);
            var slots = new int[n];
            for (int i = 0; i < n; i++) slots[i] = start + i;
            var b = Dev.BeginBatch();
            int r = RecordReadback(b, slots);
            Dev.Submit(b);
            BeginReadbackMap(r, n);
            CompleteReadback(r, slots);
        }
    }

    delegate void RunAction(int packedIndex, int firstSlot, int count);

    static void RecordRuns(ReadOnlySpan<int> slots, RunAction a)
    {
        int i = 0;
        while (i < slots.Length)
        {
            int j = i + 1;
            while (j < slots.Length && slots[j] == slots[j - 1] + 1) j++;
            a(i, slots[i], j - i);
            i = j;
        }
    }

    public void Dispose()
    {
        _ab.Dispose(); _ba.Dispose(); _kernel.Dispose();
        foreach (var b in _up) b.Dispose();
        foreach (var b in _down) b.Dispose();
        PoolA.Dispose(); PoolB.Dispose(); SlotOf.Dispose(); UniformVal.Dispose(); Active.Dispose(); ChunkOfSlot.Dispose(); Params.Dispose();
        _tsResolve?.Dispose(); _tsRead?.Dispose();
        if (QuerySet != 0) Native.Wgpu.wgpuQuerySetRelease(QuerySet);
    }
}

