using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using GpuField.Native;
using static GpuField.Native.Wgpu;

namespace GpuField.Backend;

/// <summary>wgpu-native Backend. One instance/adapter/device/queue. Not thread-safe.</summary>
public sealed unsafe class WgpuDevice : IComputeDevice
{
    public readonly nint Instance, Adapter, Device, Queue;
    public Capabilities Capabilities { get; }
    public readonly Limits AdapterLimits, DeviceLimits;
    public readonly float TimestampPeriodNs;
    public static int UncapturedErrors;

    public WgpuDevice(uint backendType, bool defaultLimits = false)
    {
        Instance = wgpuCreateInstance(null);
        if (Instance == 0) throw new InvalidOperationException("wgpuCreateInstance failed");

        var opts = new RequestAdapterOptions { featureLevel = FeatureLevel_Core, powerPreference = PowerPreference_HighPerformance, backendType = backendType };
        var res = stackalloc nint[2];
        res[0] = 0; res[1] = 0;
        var cb = new CallbackInfo { mode = CallbackMode_AllowProcessEvents, callback = (delegate* unmanaged<uint, nint, StringView, void*, void*, void>)&OnAdapter, userdata1 = res };
        wgpuInstanceRequestAdapter(Instance, &opts, cb);
        for (int i = 0; i < 1000 && res[1] == 0; i++) { wgpuInstanceProcessEvents(Instance); if (res[1] == 0) Thread.Sleep(1); }
        Adapter = res[0];
        if (Adapter == 0) throw new InvalidOperationException($"No adapter for backend {backendType}");

        AdapterInfo info = default;
        wgpuAdapterGetInfo(Adapter, &info);
        Limits al = default;
        if (wgpuAdapterGetLimits(Adapter, &al) != Status_Success) throw new InvalidOperationException("GetLimits failed");
        AdapterLimits = al;
        bool ts = wgpuAdapterHasFeature(Adapter, FeatureName_TimestampQuery) != 0;

        // Ask for everything the adapter offers: defaults (128 MiB binding, 256 MiB buffer, 8 storage buffers) are too small.
        var required = al;
        required.nextInChain = null;
        uint feature = FeatureName_TimestampQuery;
        var dd = new DeviceDescriptor
        {
            label = StringView.Null,
            requiredFeatureCount = ts ? 1u : 0u,
            requiredFeatures = &feature,
            requiredLimits = defaultLimits ? null : &required,
            defaultQueue = new QueueDescriptor { label = StringView.Null },
            deviceLostCallbackInfo = new CallbackInfo { mode = CallbackMode_AllowSpontaneous },
            uncapturedErrorCallbackInfo = new UncapturedErrorCallbackInfo { callback = (delegate* unmanaged<nint*, uint, StringView, void*, void*, void>)&OnError },
        };
        res[0] = 0; res[1] = 0;
        var dcb = new CallbackInfo { mode = CallbackMode_AllowProcessEvents, callback = (delegate* unmanaged<uint, nint, StringView, void*, void*, void>)&OnDevice, userdata1 = res };
        wgpuAdapterRequestDevice(Adapter, &dd, dcb);
        for (int i = 0; i < 1000 && res[1] == 0; i++) { wgpuInstanceProcessEvents(Instance); if (res[1] == 0) Thread.Sleep(1); }
        Device = res[0];
        if (Device == 0) throw new InvalidOperationException("RequestDevice failed");
        Queue = wgpuDeviceGetQueue(Device);
        Limits dl = default;
        wgpuDeviceGetLimits(Device, &dl);
        DeviceLimits = dl;
        TimestampPeriodNs = ts ? wgpuQueueGetTimestampPeriod(Queue) : 0;

        string backendName = info.backendType switch { 4 => "D3D12", 6 => "Vulkan", 5 => "Metal", 7 => "OpenGL", _ => $"type{info.backendType}" };
        Capabilities = new Capabilities(backendName, $"{info.device} ({info.description})".Trim(), dl.maxBufferSize, dl.maxStorageBufferBindingSize,
            dl.maxStorageBuffersPerShaderStage, dl.maxComputeWorkgroupsPerDimension, ts, HostPointerImport: false, IntegerBitExact: true);
    }

    [UnmanagedCallersOnly]
    static void OnAdapter(uint status, nint adapter, StringView msg, void* u1, void* u2)
    {
        var r = (nint*)u1;
        if (status == RequestAdapterStatus_Success) r[0] = adapter; else Console.Error.WriteLine($"RequestAdapter status {status}: {msg}");
        r[1] = 1;
    }

    [UnmanagedCallersOnly]
    static void OnDevice(uint status, nint device, StringView msg, void* u1, void* u2)
    {
        var r = (nint*)u1;
        if (status == RequestDeviceStatus_Success) r[0] = device; else Console.Error.WriteLine($"RequestDevice status {status}: {msg}");
        r[1] = 1;
    }

    [UnmanagedCallersOnly]
    static void OnError(nint* device, uint type, StringView msg, void* u1, void* u2)
    {
        Interlocked.Increment(ref UncapturedErrors);
        Console.Error.WriteLine($"[wgpu uncaptured error type={type}] {msg}");
    }

    // ---------------------------------------------------------------------------------------
    public IGpuBuffer CreateBuffer(ulong size, BufferKind kind, string label) => CreateBufferRaw(size, kind, label, mappedAtCreation: false);

    public WgpuBuffer CreateBufferRaw(ulong size, BufferKind kind, string label, bool mappedAtCreation)
    {
        ulong usage = kind switch
        {
            BufferKind.Storage => BufferUsage_Storage | BufferUsage_CopySrc | BufferUsage_CopyDst,
            BufferKind.Uniform => BufferUsage_Uniform | BufferUsage_CopyDst,
            BufferKind.UploadStaging => BufferUsage_MapWrite | BufferUsage_CopySrc,
            BufferKind.ReadbackStaging => BufferUsage_MapRead | BufferUsage_CopyDst,
            BufferKind.Timestamps => BufferUsage_QueryResolve | BufferUsage_CopySrc,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        int before = UncapturedErrors;
        fixed (byte* l = Encoding.UTF8.GetBytes(label + "\0"))
        {
            var d = new BufferDescriptor { label = new StringView { data = l, length = STRLEN }, usage = usage, size = size, mappedAtCreation = mappedAtCreation ? 1u : 0u };
            nint h = wgpuDeviceCreateBuffer(Device, &d);
            wgpuDevicePoll(Device, 0, null);
            if (h == 0 || UncapturedErrors != before) throw new InvalidOperationException($"CreateBuffer '{label}' size {size} failed");
            return new WgpuBuffer(this, h, size, kind);
        }
    }

    public IKernel CreateKernel(string name, string source, string entryPoint, ReadOnlySpan<BindingKind> layout)
    {
        int before = UncapturedErrors;
        byte[] code = Encoding.UTF8.GetBytes(source);
        byte[] entry = Encoding.UTF8.GetBytes(entryPoint);
        nint module, bgl, pl, pipe;
        fixed (byte* c = code)
        fixed (byte* e = entry)
        {
            var wgsl = new ShaderSourceWGSL { chain = new ChainedStruct { sType = SType_ShaderSourceWGSL }, code = new StringView { data = c, length = (nuint)code.Length } };
            var smd = new ShaderModuleDescriptor { nextInChain = &wgsl.chain, label = StringView.Null };
            module = wgpuDeviceCreateShaderModule(Device, &smd);

            var entries = stackalloc BindGroupLayoutEntry[layout.Length];
            for (int i = 0; i < layout.Length; i++)
            {
                entries[i] = default;
                entries[i].binding = (uint)i;
                entries[i].visibility = ShaderStage_Compute;
                entries[i].buffer.type = layout[i] switch
                {
                    BindingKind.StorageRead => BufferBindingType_ReadOnlyStorage,
                    BindingKind.StorageReadWrite => BufferBindingType_Storage,
                    _ => BufferBindingType_Uniform,
                };
            }
            var bgld = new BindGroupLayoutDescriptor { label = StringView.Null, entryCount = (nuint)layout.Length, entries = entries };
            bgl = wgpuDeviceCreateBindGroupLayout(Device, &bgld);
            var pld = new PipelineLayoutDescriptor { label = StringView.Null, bindGroupLayoutCount = 1, bindGroupLayouts = &bgl };
            pl = wgpuDeviceCreatePipelineLayout(Device, &pld);
            var cpd = new ComputePipelineDescriptor
            {
                label = StringView.Null, layout = pl,
                compute = new ComputeState { module = module, entryPoint = new StringView { data = e, length = (nuint)entry.Length } },
            };
            pipe = wgpuDeviceCreateComputePipeline(Device, &cpd);
        }
        wgpuDevicePoll(Device, 0, null);
        if (pipe == 0 || UncapturedErrors != before) throw new InvalidOperationException($"Kernel '{name}' failed to compile");
        return new WgpuKernel(name, module, bgl, pl, pipe);
    }

    public IBindingSet CreateBindings(IKernel kernel, ReadOnlySpan<(IGpuBuffer buffer, ulong offset, ulong size)> entries)
    {
        var k = (WgpuKernel)kernel;
        var e = stackalloc BindGroupEntry[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            var (b, off, size) = entries[i];
            e[i] = new BindGroupEntry { binding = (uint)i, buffer = ((WgpuBuffer)b).Handle, offset = off, size = size == 0 ? WHOLE_SIZE : size };
        }
        var d = new BindGroupDescriptor { label = StringView.Null, layout = k.BindGroupLayout, entryCount = (nuint)entries.Length, entries = e };
        int before = UncapturedErrors;
        nint g = wgpuDeviceCreateBindGroup(Device, &d);
        wgpuDevicePoll(Device, 0, null);
        if (g == 0 || UncapturedErrors != before) throw new InvalidOperationException($"BindGroup for '{k.Name}' failed");
        return new WgpuBindings(g);
    }

    public ICommandBatch BeginBatch() => new WgpuBatch(this, wgpuDeviceCreateCommandEncoder(Device, null));

    public IFence Submit(ICommandBatch batch)
    {
        var b = (WgpuBatch)batch;
        nint cmd = wgpuCommandEncoderFinish(b.Encoder, null);
        wgpuCommandEncoderRelease(b.Encoder);
        ulong idx = wgpuQueueSubmitForIndex(Queue, 1, &cmd);
        wgpuCommandBufferRelease(cmd);
        return new WgpuFence(this, idx);
    }

    public void Write(IGpuBuffer dst, ulong offset, void* src, nuint size) => wgpuQueueWriteBuffer(Queue, ((WgpuBuffer)dst).Handle, offset, src, size);

    /// <summary>Blocking poll until the whole queue is idle.</summary>
    public void WaitIdle() => wgpuDevicePoll(Device, 1, null);

    public nint CreateTimestampQuerySet(uint count)
    {
        var d = new QuerySetDescriptor { label = StringView.Null, type = QueryType_Timestamp, count = count };
        return wgpuDeviceCreateQuerySet(Device, &d);
    }

    public void Dispose()
    {
        wgpuQueueRelease(Queue);
        wgpuDeviceRelease(Device);
        wgpuAdapterRelease(Adapter);
        wgpuInstanceRelease(Instance);
    }
}

public sealed unsafe class WgpuBuffer(WgpuDevice dev, nint handle, ulong size, BufferKind kind) : IGpuBuffer
{
    public readonly nint Handle = handle;
    public ulong Size { get; } = size;
    public BufferKind Kind { get; } = kind;
    // Map status lives in native memory so the callback never touches a movable managed object.
    readonly int* _mapState = (int*)NativeMemory.AllocZeroed(4); // 0 pending, 1 ok, 2 failed

    /// <summary>Starts an async map. Completion is observed by <see cref="TryFinishMap"/> or <see cref="FinishMap"/>.</summary>
    public void BeginMap(bool write, nuint offset = 0, nuint size = 0)
    {
        *_mapState = 0;
        var cb = new CallbackInfo { mode = CallbackMode_AllowProcessEvents, callback = (delegate* unmanaged<uint, StringView, void*, void*, void>)&OnMapped, userdata1 = _mapState };
        wgpuBufferMapAsync(Handle, write ? MapMode_Write : MapMode_Read, offset, size == 0 ? (nuint)Size : size, cb);
    }

    public bool TryFinishMap()
    {
        if (Volatile.Read(ref *_mapState) == 0) { wgpuDevicePoll(dev.Device, 0, null); wgpuInstanceProcessEvents(dev.Instance); }
        int s = Volatile.Read(ref *_mapState);
        if (s == 2) throw new InvalidOperationException("MapAsync failed");
        return s == 1;
    }

    public void FinishMap()
    {
        while (Volatile.Read(ref *_mapState) == 0) { wgpuDevicePoll(dev.Device, 1, null); wgpuInstanceProcessEvents(dev.Instance); }
        if (*_mapState == 2) throw new InvalidOperationException("MapAsync failed");
    }

    public void* MappedRange(nuint offset, nuint size) => wgpuBufferGetMappedRange(Handle, offset, size);
    public void* ConstMappedRange(nuint offset, nuint size) => wgpuBufferGetConstMappedRange(Handle, offset, size);
    public void Unmap() => wgpuBufferUnmap(Handle);

    [UnmanagedCallersOnly]
    static void OnMapped(uint status, StringView msg, void* u1, void* u2)
    {
        if (status != MapAsyncStatus_Success) Console.Error.WriteLine($"Map failed {status}: {msg}");
        Volatile.Write(ref *(int*)u1, status == MapAsyncStatus_Success ? 1 : 2);
    }

    public void Dispose() { wgpuBufferDestroy(Handle); wgpuBufferRelease(Handle); NativeMemory.Free(_mapState); }
}

public sealed class WgpuKernel(string name, nint module, nint bgl, nint pl, nint pipeline) : IKernel
{
    public string Name { get; } = name;
    public readonly nint Module = module, BindGroupLayout = bgl, PipelineLayout = pl, Pipeline = pipeline;
    public void Dispose()
    {
        Wgpu.wgpuComputePipelineRelease(Pipeline); Wgpu.wgpuPipelineLayoutRelease(PipelineLayout);
        Wgpu.wgpuBindGroupLayoutRelease(BindGroupLayout); Wgpu.wgpuShaderModuleRelease(Module);
    }
}

public sealed class WgpuBindings(nint group) : IBindingSet
{
    public readonly nint Group = group;
    public void Dispose() => Wgpu.wgpuBindGroupRelease(Group);
}

public sealed unsafe class WgpuBatch(WgpuDevice dev, nint encoder) : ICommandBatch
{
    public readonly nint Encoder = encoder;
    public nint TimestampQuerySet;

    public void Dispatch(IKernel kernel, IBindingSet bindings, uint groupsX, uint groupsY, int timestampPair = -1)
    {
        var k = (WgpuKernel)kernel;
        var tw = new PassTimestampWrites { querySet = TimestampQuerySet, beginningOfPassWriteIndex = (uint)(timestampPair * 2), endOfPassWriteIndex = (uint)(timestampPair * 2 + 1) };
        var pd = new ComputePassDescriptor { label = StringView.Null, timestampWrites = timestampPair >= 0 ? &tw : null };
        nint pass = wgpuCommandEncoderBeginComputePass(Encoder, &pd);
        wgpuComputePassEncoderSetPipeline(pass, k.Pipeline);
        wgpuComputePassEncoderSetBindGroup(pass, 0, ((WgpuBindings)bindings).Group, 0, null);
        wgpuComputePassEncoderDispatchWorkgroups(pass, groupsX, groupsY, 1);
        wgpuComputePassEncoderEnd(pass);
        wgpuComputePassEncoderRelease(pass);
    }

    public void Copy(IGpuBuffer src, ulong srcOffset, IGpuBuffer dst, ulong dstOffset, ulong size) =>
        wgpuCommandEncoderCopyBufferToBuffer(Encoder, ((WgpuBuffer)src).Handle, srcOffset, ((WgpuBuffer)dst).Handle, dstOffset, size);

    public void ResolveTimestamps(uint first, uint count, IGpuBuffer dst, ulong offset) =>
        wgpuCommandEncoderResolveQuerySet(Encoder, TimestampQuerySet, first, count, ((WgpuBuffer)dst).Handle, offset);
}

public sealed unsafe class WgpuFence(WgpuDevice dev, ulong submissionIndex) : IFence
{
    public readonly ulong SubmissionIndex = submissionIndex;
    bool _done;

    // wgpu-native has no per-submission non-blocking query; wgpuDevicePoll(wait=false) returns "queue empty".
    // That is conservative (true only once everything up to now is done), which is fine for k=1 latency.
    public bool IsComplete => _done || (_done = wgpuDevicePoll(dev.Device, 0, null) != 0);

    public void Wait()
    {
        if (_done) return;
        ulong idx = SubmissionIndex;
        wgpuDevicePoll(dev.Device, 1, &idx);
        _done = true;
    }
}

