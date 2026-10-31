// Backend abstraction (copied from prototypes/gpu-field, extended): Device -> Buffer, Kernel, CommandBatch with
// explicit compute passes, indirect dispatch and dynamic uniform offsets, Fence, Capabilities.
namespace GpuPath.Backend;

public sealed record Capabilities(
    string BackendName,
    string AdapterName,
    ulong MaxBufferSize,
    ulong MaxStorageBufferBindingSize,
    uint MaxStorageBuffersPerStage,
    uint MaxWorkgroupsPerDimension,
    bool TimestampQuery,
    bool HostPointerImport,
    bool IntegerBitExact);

public enum BufferKind { Storage, StorageIndirect, Uniform, UploadStaging, ReadbackStaging, Timestamps }

public enum BindingKind { StorageRead, StorageReadWrite, Uniform, UniformDynamic }

public interface IGpuBuffer : IDisposable { ulong Size { get; } }
public interface IKernel : IDisposable { string Name { get; } }
public interface IBindingSet : IDisposable { }

/// <summary>Recorded GPU commands. Dispatches go inside an explicit compute pass (one pass = one timestamp pair).</summary>
public interface ICommandBatch
{
    void BeginPass(int timestampPair = -1);
    void SetKernel(IKernel kernel, IBindingSet bindings, uint dynamicOffset = uint.MaxValue);
    void Dispatch(uint x, uint y = 1, uint z = 1);
    void DispatchIndirect(IGpuBuffer args, ulong offset);
    void EndPass();
    void Copy(IGpuBuffer src, ulong srcOffset, IGpuBuffer dst, ulong dstOffset, ulong size);
    void Clear(IGpuBuffer dst, ulong offset, ulong size);
}

public interface IFence { bool IsComplete { get; } void Wait(); }

public interface IComputeDevice : IDisposable
{
    Capabilities Capabilities { get; }
    IGpuBuffer CreateBuffer(ulong size, BufferKind kind, string label);
    IKernel CreateKernel(string name, string source, string entryPoint, ReadOnlySpan<BindingKind> layout);
    IBindingSet CreateBindings(IKernel kernel, ReadOnlySpan<(IGpuBuffer buffer, ulong offset, ulong size)> entries);
    ICommandBatch BeginBatch();
    IFence Submit(ICommandBatch batch);
    unsafe void Write(IGpuBuffer dst, ulong offset, void* src, nuint size);
}
