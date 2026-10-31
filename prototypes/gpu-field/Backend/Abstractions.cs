// Sketch of the Backend abstraction shape the prototype exercises. Deliberately small.
// Device -> Buffer, Kernel (compiled pipeline), CommandBatch, Fence, Capabilities.
// Upload/readback are explicit; nothing assumes CPU and GPU share memory.
namespace GpuField.Backend;

/// <summary>What a Backend can do. Kernels and Field layouts consult this, never the concrete Backend.</summary>
public sealed record Capabilities(
    string BackendName,
    string AdapterName,
    ulong MaxBufferSize,
    ulong MaxStorageBufferBindingSize,
    uint MaxStorageBuffersPerStage,
    uint MaxWorkgroupsPerDimension,
    bool TimestampQuery,
    bool HostPointerImport,   // zero-copy import of CPU memory (Vulkan VK_EXT_external_memory_host, CUDA register). wgpu: false.
    bool IntegerBitExact);    // i32 add/sub/shift identical to CPU (true for WGSL: wrapping i32, arithmetic >>).

public enum BufferKind
{
    Storage,       // device-local, bound to Kernels, copy src/dst
    Uniform,       // small parameter block
    UploadStaging, // host-visible, CPU writes, copy src
    ReadbackStaging, // host-visible, CPU reads, copy dst
    Timestamps,    // query resolve target
}

public enum BindingKind { StorageRead, StorageReadWrite, Uniform }

public interface IGpuBuffer : IDisposable
{
    ulong Size { get; }
}

/// <summary>A compiled compute pipeline plus its binding layout. Paired with a CPU reference implementation.</summary>
public interface IKernel : IDisposable
{
    string Name { get; }
}

public interface IBindingSet : IDisposable { }

/// <summary>A recorded batch of GPU commands. Recorded on one thread, submitted once.</summary>
public interface ICommandBatch
{
    void Dispatch(IKernel kernel, IBindingSet bindings, uint groupsX, uint groupsY, int timestampPair = -1);
    void Copy(IGpuBuffer src, ulong srcOffset, IGpuBuffer dst, ulong dstOffset, ulong size);
}

/// <summary>Completion handle for one submitted batch. Deferred completion: poll IsComplete, or Wait when late.</summary>
public interface IFence
{
    bool IsComplete { get; }
    void Wait();
}

public interface IComputeDevice : IDisposable
{
    Capabilities Capabilities { get; }
    IGpuBuffer CreateBuffer(ulong size, BufferKind kind, string label);
    IKernel CreateKernel(string name, string source, string entryPoint, ReadOnlySpan<BindingKind> layout);
    IBindingSet CreateBindings(IKernel kernel, ReadOnlySpan<(IGpuBuffer buffer, ulong offset, ulong size)> entries);
    ICommandBatch BeginBatch();
    IFence Submit(ICommandBatch batch);
    /// <summary>Copying write (wgpu: queue.writeBuffer, staged internally by the Backend).</summary>
    unsafe void Write(IGpuBuffer dst, ulong offset, void* src, nuint size);
}
