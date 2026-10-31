// Hand-written minimal bindings for wgpu-native v29.0.1.1 (webgpu.h + wgpu.h).
// Only the compute subset this prototype needs. All structs are blittable and laid out
// sequentially with natural alignment, which matches the C headers on win-x64.
// Every entry point is a [LibraryImport] with only pointer / primitive / blittable-struct
// parameters, so the source generator emits no marshalling code (Native AOT friendly).
using System.Runtime.InteropServices;

namespace GpuPath.Native;

public static unsafe partial class Wgpu
{
    const string Lib = "wgpu_native";

    // ---- constants (webgpu.h) -------------------------------------------------------------
    public static readonly nuint STRLEN = nuint.MaxValue;
    public const ulong WHOLE_SIZE = ulong.MaxValue;

    public const ulong BufferUsage_MapRead = 0x1, BufferUsage_MapWrite = 0x2, BufferUsage_CopySrc = 0x4,
        BufferUsage_CopyDst = 0x8, BufferUsage_Uniform = 0x40, BufferUsage_Storage = 0x80, BufferUsage_Indirect = 0x100, BufferUsage_QueryResolve = 0x200;
    public const ulong MapMode_Read = 0x1, MapMode_Write = 0x2;
    public const ulong ShaderStage_Compute = 0x4;

    public const uint BufferBindingType_Uniform = 2, BufferBindingType_Storage = 3, BufferBindingType_ReadOnlyStorage = 4;
    public const uint CallbackMode_WaitAnyOnly = 1, CallbackMode_AllowProcessEvents = 2, CallbackMode_AllowSpontaneous = 3;
    public const uint Status_Success = 1;
    public const uint RequestAdapterStatus_Success = 1, RequestDeviceStatus_Success = 1, MapAsyncStatus_Success = 1;
    public const uint FeatureName_TimestampQuery = 9;
    public const uint QueryType_Timestamp = 2;
    public const uint PowerPreference_HighPerformance = 2;
    public const uint FeatureLevel_Core = 2;
    public const uint SType_ShaderSourceWGSL = 2;
    public const uint BackendType_Undefined = 0, BackendType_D3D12 = 4, BackendType_Vulkan = 6;

    // ---- basic structs --------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    public struct StringView
    {
        public byte* data; public nuint length;
        public static StringView Null => new() { data = null, length = STRLEN };
        public override string ToString() => data == null ? "" :
            length == STRLEN ? Marshal.PtrToStringUTF8((nint)data) ?? "" : System.Text.Encoding.UTF8.GetString(data, checked((int)length));
    }

    [StructLayout(LayoutKind.Sequential)] public struct ChainedStruct { public ChainedStruct* next; public uint sType; }
    [StructLayout(LayoutKind.Sequential)] public struct Future { public ulong id; }

    [StructLayout(LayoutKind.Sequential)]
    public struct CallbackInfo   // shared shape of BufferMap/QueueWorkDone/RequestAdapter/RequestDevice/DeviceLost CallbackInfo
    {
        public ChainedStruct* nextInChain; public uint mode; public void* callback; public void* userdata1; public void* userdata2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UncapturedErrorCallbackInfo { public ChainedStruct* nextInChain; public void* callback; public void* userdata1; public void* userdata2; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RequestAdapterOptions
    {
        public ChainedStruct* nextInChain; public uint featureLevel; public uint powerPreference; public uint forceFallbackAdapter;
        public uint backendType; public nint compatibleSurface;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AdapterInfo
    {
        public ChainedStruct* nextInChain; public StringView vendor, architecture, device, description;
        public uint backendType, adapterType, vendorID, deviceID, subgroupMinSize, subgroupMaxSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Limits
    {
        public ChainedStruct* nextInChain;
        public uint maxTextureDimension1D, maxTextureDimension2D, maxTextureDimension3D, maxTextureArrayLayers, maxBindGroups,
            maxBindGroupsPlusVertexBuffers, maxBindingsPerBindGroup, maxDynamicUniformBuffersPerPipelineLayout,
            maxDynamicStorageBuffersPerPipelineLayout, maxSampledTexturesPerShaderStage, maxSamplersPerShaderStage,
            maxStorageBuffersPerShaderStage, maxStorageTexturesPerShaderStage, maxUniformBuffersPerShaderStage;
        public ulong maxUniformBufferBindingSize, maxStorageBufferBindingSize;
        public uint minUniformBufferOffsetAlignment, minStorageBufferOffsetAlignment, maxVertexBuffers;
        public ulong maxBufferSize;
        public uint maxVertexAttributes, maxVertexBufferArrayStride, maxInterStageShaderVariables, maxColorAttachments,
            maxColorAttachmentBytesPerSample, maxComputeWorkgroupStorageSize, maxComputeInvocationsPerWorkgroup,
            maxComputeWorkgroupSizeX, maxComputeWorkgroupSizeY, maxComputeWorkgroupSizeZ, maxComputeWorkgroupsPerDimension, maxImmediateSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct QueueDescriptor { public ChainedStruct* nextInChain; public StringView label; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceDescriptor
    {
        public ChainedStruct* nextInChain; public StringView label; public nuint requiredFeatureCount; public uint* requiredFeatures;
        public Limits* requiredLimits; public QueueDescriptor defaultQueue; public CallbackInfo deviceLostCallbackInfo;
        public UncapturedErrorCallbackInfo uncapturedErrorCallbackInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BufferDescriptor { public ChainedStruct* nextInChain; public StringView label; public ulong usage; public ulong size; public uint mappedAtCreation; }

    [StructLayout(LayoutKind.Sequential)]
    public struct ShaderSourceWGSL { public ChainedStruct chain; public StringView code; }

    [StructLayout(LayoutKind.Sequential)]
    public struct ShaderModuleDescriptor { public ChainedStruct* nextInChain; public StringView label; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BufferBindingLayout { public ChainedStruct* nextInChain; public uint type; public uint hasDynamicOffset; public ulong minBindingSize; }
    [StructLayout(LayoutKind.Sequential)] public struct SamplerBindingLayout { public ChainedStruct* nextInChain; public uint type; }
    [StructLayout(LayoutKind.Sequential)] public struct TextureBindingLayout { public ChainedStruct* nextInChain; public uint sampleType, viewDimension, multisampled; }
    [StructLayout(LayoutKind.Sequential)] public struct StorageTextureBindingLayout { public ChainedStruct* nextInChain; public uint access, format, viewDimension; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BindGroupLayoutEntry
    {
        public ChainedStruct* nextInChain; public uint binding; public ulong visibility; public uint bindingArraySize;
        public BufferBindingLayout buffer; public SamplerBindingLayout sampler; public TextureBindingLayout texture;
        public StorageTextureBindingLayout storageTexture;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BindGroupLayoutDescriptor { public ChainedStruct* nextInChain; public StringView label; public nuint entryCount; public BindGroupLayoutEntry* entries; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BindGroupEntry { public ChainedStruct* nextInChain; public uint binding; public nint buffer; public ulong offset; public ulong size; public nint sampler; public nint textureView; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BindGroupDescriptor { public ChainedStruct* nextInChain; public StringView label; public nint layout; public nuint entryCount; public BindGroupEntry* entries; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PipelineLayoutDescriptor { public ChainedStruct* nextInChain; public StringView label; public nuint bindGroupLayoutCount; public nint* bindGroupLayouts; public uint immediateSize; }

    [StructLayout(LayoutKind.Sequential)]
    public struct ComputeState { public ChainedStruct* nextInChain; public nint module; public StringView entryPoint; public nuint constantCount; public void* constants; }

    [StructLayout(LayoutKind.Sequential)]
    public struct ComputePipelineDescriptor { public ChainedStruct* nextInChain; public StringView label; public nint layout; public ComputeState compute; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PassTimestampWrites { public ChainedStruct* nextInChain; public nint querySet; public uint beginningOfPassWriteIndex; public uint endOfPassWriteIndex; }

    [StructLayout(LayoutKind.Sequential)]
    public struct ComputePassDescriptor { public ChainedStruct* nextInChain; public StringView label; public PassTimestampWrites* timestampWrites; }

    [StructLayout(LayoutKind.Sequential)]
    public struct QuerySetDescriptor { public ChainedStruct* nextInChain; public StringView label; public uint type; public uint count; }

    // ---- entry points: webgpu.h ----------------------------------------------------------
    [LibraryImport(Lib)] public static partial nint wgpuCreateInstance(void* descriptor);
    [LibraryImport(Lib)] public static partial Future wgpuInstanceRequestAdapter(nint instance, RequestAdapterOptions* options, CallbackInfo callbackInfo);
    [LibraryImport(Lib)] public static partial void wgpuInstanceProcessEvents(nint instance);
    [LibraryImport(Lib)] public static partial void wgpuInstanceRelease(nint instance);

    [LibraryImport(Lib)] public static partial uint wgpuAdapterGetInfo(nint adapter, AdapterInfo* info);
    [LibraryImport(Lib)] public static partial uint wgpuAdapterGetLimits(nint adapter, Limits* limits);
    [LibraryImport(Lib)] public static partial uint wgpuAdapterHasFeature(nint adapter, uint feature);
    [LibraryImport(Lib)] public static partial Future wgpuAdapterRequestDevice(nint adapter, DeviceDescriptor* descriptor, CallbackInfo callbackInfo);
    [LibraryImport(Lib)] public static partial void wgpuAdapterRelease(nint adapter);

    [LibraryImport(Lib)] public static partial uint wgpuDeviceGetLimits(nint device, Limits* limits);
    [LibraryImport(Lib)] public static partial nint wgpuDeviceGetQueue(nint device);
    [LibraryImport(Lib)] public static partial nint wgpuDeviceCreateBuffer(nint device, BufferDescriptor* descriptor);
    [LibraryImport(Lib)] public static partial nint wgpuDeviceCreateShaderModule(nint device, ShaderModuleDescriptor* descriptor);
    [LibraryImport(Lib)] public static partial nint wgpuDeviceCreateBindGroupLayout(nint device, BindGroupLayoutDescriptor* descriptor);
    [LibraryImport(Lib)] public static partial nint wgpuDeviceCreateBindGroup(nint device, BindGroupDescriptor* descriptor);
    [LibraryImport(Lib)] public static partial nint wgpuDeviceCreatePipelineLayout(nint device, PipelineLayoutDescriptor* descriptor);
    [LibraryImport(Lib)] public static partial nint wgpuDeviceCreateComputePipeline(nint device, ComputePipelineDescriptor* descriptor);
    [LibraryImport(Lib)] public static partial nint wgpuDeviceCreateCommandEncoder(nint device, void* descriptor);
    [LibraryImport(Lib)] public static partial nint wgpuDeviceCreateQuerySet(nint device, QuerySetDescriptor* descriptor);
    [LibraryImport(Lib)] public static partial void wgpuDeviceRelease(nint device);

    [LibraryImport(Lib)] public static partial nint wgpuCommandEncoderBeginComputePass(nint encoder, ComputePassDescriptor* descriptor);
    [LibraryImport(Lib)] public static partial void wgpuCommandEncoderCopyBufferToBuffer(nint encoder, nint src, ulong srcOffset, nint dst, ulong dstOffset, ulong size);
    [LibraryImport(Lib)] public static partial void wgpuCommandEncoderResolveQuerySet(nint encoder, nint querySet, uint firstQuery, uint queryCount, nint dst, ulong dstOffset);
    [LibraryImport(Lib)] public static partial nint wgpuCommandEncoderFinish(nint encoder, void* descriptor);
    [LibraryImport(Lib)] public static partial void wgpuCommandEncoderRelease(nint encoder);
    [LibraryImport(Lib)] public static partial void wgpuCommandBufferRelease(nint commandBuffer);

    [LibraryImport(Lib)] public static partial void wgpuComputePassEncoderSetPipeline(nint pass, nint pipeline);
    [LibraryImport(Lib)] public static partial void wgpuComputePassEncoderSetBindGroup(nint pass, uint groupIndex, nint group, nuint dynamicOffsetCount, uint* dynamicOffsets);
    [LibraryImport(Lib)] public static partial void wgpuComputePassEncoderDispatchWorkgroups(nint pass, uint x, uint y, uint z);
    [LibraryImport(Lib)] public static partial void wgpuComputePassEncoderDispatchWorkgroupsIndirect(nint pass, nint buffer, ulong offset);
    [LibraryImport(Lib)] public static partial void wgpuCommandEncoderClearBuffer(nint encoder, nint buffer, ulong offset, ulong size);
    [LibraryImport(Lib)] public static partial void wgpuComputePassEncoderEnd(nint pass);
    [LibraryImport(Lib)] public static partial void wgpuComputePassEncoderRelease(nint pass);

    [LibraryImport(Lib)] public static partial void wgpuQueueSubmit(nint queue, nuint count, nint* commands);
    [LibraryImport(Lib)] public static partial void wgpuQueueWriteBuffer(nint queue, nint buffer, ulong offset, void* data, nuint size);
    [LibraryImport(Lib)] public static partial Future wgpuQueueOnSubmittedWorkDone(nint queue, CallbackInfo callbackInfo);

    [LibraryImport(Lib)] public static partial Future wgpuBufferMapAsync(nint buffer, ulong mode, nuint offset, nuint size, CallbackInfo callbackInfo);
    [LibraryImport(Lib)] public static partial void* wgpuBufferGetMappedRange(nint buffer, nuint offset, nuint size);
    [LibraryImport(Lib)] public static partial void* wgpuBufferGetConstMappedRange(nint buffer, nuint offset, nuint size);
    [LibraryImport(Lib)] public static partial void wgpuBufferUnmap(nint buffer);
    [LibraryImport(Lib)] public static partial void wgpuBufferDestroy(nint buffer);
    [LibraryImport(Lib)] public static partial void wgpuBufferRelease(nint buffer);

    [LibraryImport(Lib)] public static partial void wgpuShaderModuleRelease(nint m);
    [LibraryImport(Lib)] public static partial void wgpuBindGroupRelease(nint g);
    [LibraryImport(Lib)] public static partial void wgpuBindGroupLayoutRelease(nint g);
    [LibraryImport(Lib)] public static partial void wgpuPipelineLayoutRelease(nint g);
    [LibraryImport(Lib)] public static partial void wgpuComputePipelineRelease(nint p);
    [LibraryImport(Lib)] public static partial void wgpuQuerySetRelease(nint q);
    [LibraryImport(Lib)] public static partial void wgpuQueueRelease(nint q);

    // ---- entry points: wgpu.h (native extensions) ----------------------------------------
    [LibraryImport(Lib)] public static partial ulong wgpuQueueSubmitForIndex(nint queue, nuint count, nint* commands);
    [LibraryImport(Lib)] public static partial float wgpuQueueGetTimestampPeriod(nint queue);
    [LibraryImport(Lib)] public static partial uint wgpuDevicePoll(nint device, uint wait, ulong* submissionIndex);
    [LibraryImport(Lib)] public static partial uint wgpuGetVersion();
    [LibraryImport(Lib, EntryPoint = "wgpuGetVersion"), SuppressGCTransition] public static partial uint wgpuGetVersionNoTransition();

    // A no-op entry point used to measure raw P/Invoke transition cost.
    // wgpuGetVersion just returns a constant, so its cost is dominated by the transition.
}

