# GPU compute options from .NET 11 (Windows, Linux, macOS bonus)

Research for TASK-1.2. Feeds TASK-1.9 (Backend abstraction and first Backend). Facts gathered 2026-10-30 from first-party repositories (README, csproj, releases, source tree, issues) via the GitHub API and web fetch. Items marked **(inferred)** are my reading of primary evidence, not a stated claim. Items marked **(unverified)** could not be confirmed from a primary source.

Terminology: "Backend" per GLOSSARY.md. "Unmanaged CPU memory interop" means: can the Framework's native/pinned Field memory be shared with the GPU without an extra managed copy.

## Summary table

| Option | Compute | Platforms | Native AOT | Maintenance (as of 2026-10-30) | Binding effort | Verdict |
|---|---|---|---|---|---|---|
| NVRHI | Yes (D3D11/D3D12/Vulkan 1.3) | Windows, Linux (x64, ARM64). No Metal | n/a (C++); a managed wrapper can be AOT-friendly | Very active (push 2026-10-28), no tagged releases | High: no C API, none planned, no C# bindings found | Viable only if you write a C shim plus bindings |
| Silk.NET Vulkan (2.x) | Yes (raw Vulkan) | Win, Linux, macOS via MoltenVK | Not annotated; trimming annotation deferred to 3.0 | 2.x maintenance mode, last 2.23.0 (2026-01-22); 3.0 in development | High (raw Vulkan) | Solid raw foundation, lots of code |
| Silk.NET WebGPU (2.x) | Yes | Win, Linux, macOS | As above | As above, tracks old wgpu | Medium | Prefer Alimer or generated wgpu-native bindings |
| Veldrid | Yes (compute pipelines, RW structured buffers) | Win, Linux, macOS | Not stated; targets netstandard2.0 and net6.0 | Effectively dormant (last release 4.9.0, 2023-02-03; author stopped public updates) | Low | Not recommended for a new long-lived dependency |
| ILGPU | Yes (C# kernels) | Win, Linux (CUDA, OpenCL, CPU); Metal and ROCm backends exist in v2 | v1.5.x is JIT. v2 beta adds build-time kernel compiler (ILGPUC) | Active; v1.5.3 stable (2025-07-12), v2.0.0-beta1 on main, net10.0 target | Lowest (pure C#) | Strongest managed-only candidate |
| ComputeSharp | Yes (C# to HLSL, D3D12) | Windows only | Not stated in README (unverified) | Maintained, slower cadence (3.2.0 2025-04-14, commits through 2026-09) | Lowest on Windows | Windows-only: cannot be the sole Backend |
| wgpu-native (WebGPU C API) | Yes (compute pipelines, WGSL) | Win (DX12/Vulkan), Linux (Vulkan), macOS (Metal) | Good fit: plain C function pointers | Very active (v29.0.1.1, 2026-06-23) | Medium: bindings exist | Best cross-platform native option |
| CUDA bindings (ManagedCuda etc.) | Yes | NVIDIA only (Win, Linux) | Not stated | ManagedCuda active, but GPLv3 or commercial licence | Low | Optional NVIDIA-only fast path, licence issue |

## NVRHI (verified facts)

- Graphics APIs: "Direct3D 11, Direct3D 12, and Vulkan 1.3". Platforms: "Windows and Linux (x64 and ARM64)". No Metal or macOS. Source: https://github.com/NVIDIA-RTX/NVRHI README.
- Compute: "Supports all types of pipelines: Graphics, Compute, Ray Tracing, and Meshlet." Same README.
- License MIT (GitHub repo metadata). C++17, CMake 3.10+, 32-bit builds unsupported. Backends are separate CMake targets `nvrhi_d3d11`, `nvrhi_d3d12`, `nvrhi_vk`. Can be built as a single shared library. Same README.
- Activity: last push 2026-10-28; Releases section empty (no tagged releases, so consumers pin commits).
- C API: maintainer answer in issue https://github.com/NVIDIAGameWorks/nvrhi/issues/36 (repo now NVIDIA-RTX/NVRHI): "There is no C API for NVRHI, and it's not currently planned." A different NVIDIA library, NRI, was mentioned in the same thread as having a C API; it is lower-level and requires more user code.
- Existing C# bindings: none found. A GitHub repository search for "nvrhi" returned only C++ projects, a Beef port (jayrulez/Sedulous.nvrhi, last push 2022) and a Go Hikvision exporter unrelated to the library. A web search likewise found no C# wrapper. Absence is not proof; treat as "none known".
- Binding effort: NVRHI's API is C++ classes with virtual interfaces and reference-counted handles (`nvrhi::IDevice`, etc.), so P/Invoke cannot reach it directly. You would need to write a C shim (flat `extern "C"` functions, opaque handles) or a C++/CLI-style layer (not available on Linux), then bind it from C#. Shaders are authored per API (HLSL/DXIL/SPIR-V); NVRHI does not translate shaders. **(inferred from the repo's structure and shader tooling, not stated in the README).**
- Interop with unmanaged memory: NVRHI exposes buffer creation and mapping/upload through its device and command-list API; importing an arbitrary host pointer is not documented in the README **(unverified)**. Plan on staging or mapped-upload buffers.
- macOS: no support, so it cannot be the only Backend if macOS matters.

## Silk.NET (Vulkan, WebGPU, OpenCL)

- 2.X is maintenance mode: "Silk.NET 2.X updates are now released ad-hoc", 3.0 "hard at work". https://github.com/dotnet/Silk.NET README. Latest release v2.23.0, 2026-01-22; previous 2.22.0 2024-11-04 (GitHub releases).
- 3.0 branch `develop/3.0` is active (commit 2026-10-25) and its `sources` tree contains Vulkan and OpenCL but no WebGPU directory **(inferred from directory listing)**. 3.0 has no stable release.
- Bindings are generated from the Khronos headers, so you get the entire Vulkan API (including `VK_EXT_external_memory_host`). That extension imports a host allocation as device memory; address and size must be multiples of `minImportedHostPointerAlignment`, and the allocation must outlive the memory object (https://docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_external_memory_host.html). This is the strongest zero-copy path for unmanaged memory but is optional and driver-dependent (check per device).
- Native AOT/trimming: issue https://github.com/dotnet/Silk.NET/issues/305 ("Trimming") is closed; a maintainer said annotating everything in 2.X would be "a maintenance nightmare" given multitargeting and that it would be done in 3.0. Treat 2.X as not formally AOT-annotated; may still work via function-pointer loading but is unverified.
- Binding effort: writing a compute path in raw Vulkan (instance, device, memory, descriptor sets, pipelines, sync) is large. SPIR-V compilation from HLSL/GLSL/Slang is an external tool step.
- Platforms: Windows, Linux; macOS needs MoltenVK (https://github.com/gfx-rs/wgpu README table marks Vulkan on macOS as requiring MoltenVK, same applies here) **(inferred)**.

## Veldrid

- Backends: Direct3D 11, Vulkan, Metal, OpenGL 3, OpenGL ES 3. https://github.com/veldrid/veldrid README.
- Compute: yes. Source has `ComputePipelineDescription.cs` and `ResourceKind` includes `StructuredBufferReadWrite`. Shaders via Veldrid.SPIRV (separate project).
- Maintenance: README says "As of February 2023, I'm no longer able to publicly share updates." Last GitHub release v4.9.0 on 2023-02-03; last repository push 2026-03-17 (minor). Targets `netstandard2.0;net6.0` (csproj). Effectively unmaintained for new .NET versions.
- AOT: not stated. Unmanaged buffer interop: `UpdateBuffer(buffer, offset, IntPtr, size)`-style APIs exist **(unverified in this pass)**; no host-pointer import.

## ILGPU

- Pure C# GPU compiler; "entirely written in C# without any native dependencies" (README, https://github.com/m4rs-mt/ILGPU).
- Releases: v1.5.3 stable 2025-07-12 (release notes: Cuda compatibility fixes). `main` is `2.0.0-beta1` targeting `net10.0` (Src/Directory.Build.props), commits as recent as 2026-10-18. Licence shown as NOASSERTION by GitHub (custom ILGPU License; read it before adopting).
- Runtime backends in source: CPU, Cuda, OpenCL, Metal, ROCm (`Src/ILGPU/Runtime/*`). The README still says OpenCL/Cuda/CPU; Metal and ROCm folders are on `main` (v2) only **(inferred)**.
- Native AOT: ILGPUC (https://github.com/m4rs-mt/ILGPU/blob/main/Src/ILGPUC/PACKAGE.md) is "ILGPU's AOT compiler toolchain. Compiles GPU kernels at MSBuild time", with backends such as CPU, Metal, Cuda selectable via `ILGPUBackend`. v1.x JIT-compiles kernels at runtime via IL reflection, which is hostile to Native AOT. v2 is beta, so AOT friendliness is promising but unproven; the OpenCL option in `ILGPUBackend` is not shown in the doc excerpt I read **(unverified)**.
- Unmanaged interop: `PageLockScope<T>` accepts a host pointer for page-locking (source `Runtime/PageLockScope.cs`), and `CPUMemoryBuffer` wraps host memory. Direct wrapping of an arbitrary unmanaged pointer as an `ArrayView` on a GPU accelerator is not confirmed; expect explicit copy (`CopyFromCPU`) or page-locked staging **(unverified)**.
- Vulkan/D3D12 are not ILGPU backends. On Windows without CUDA, OpenCL is the only GPU route for AMD/Intel and it is increasingly absent on consumer drivers **(my domain knowledge, not verified here)**.

## ComputeSharp

- C# to HLSL compute shaders on DX12 (and D2D1 pixel shaders). Dependency on TerraFX.Interop.Windows. https://github.com/Sergio0694/ComputeSharp README.
- Windows only: README packages are UWP/WinUI/D3D12; csproj sets `SupportedOSPlatformVersion` 6.2 (Windows). Targets net10.0.
- Maintenance: v3.2.0 2025-04-14; commits through 2026-09-04. Licence MIT.
- Native AOT: README does not state it **(unverified)**; source-generator design suggests it is intended, but confirm before relying on it.
- Fits a Windows-only Backend or a prototyping tool. Not viable for Linux.

## wgpu-native (WebGPU C API)

- Rust `wgpu` runs on Vulkan, Metal, D3D12, OpenGL (https://github.com/gfx-rs/wgpu README table); wgpu-native exposes it as a C library via `webgpu.h` + `wgpu.h`. Pre-built binaries for Windows, Linux, macOS (https://github.com/gfx-rs/wgpu-native README).
- Compute: yes. The repo's `examples/compute/main.c` uses `wgpuDeviceCreateComputePipeline`, `wgpuComputePassEncoderDispatchWorkgroups`, and `wgpuBufferMapAsync`. Shaders are WGSL (SPIR-V passthrough is a wgpu feature, not verified here).
- Release v29.0.1.1 on 2026-06-23, push 2026-10-26. Apache-2.0. Tracks upstream wgpu closely; the API is still evolving (header changes between majors).
- .NET bindings listed in the README: trivaxy/WGPU.NET (last push 2023-11-10, stale), dotnet/Silk.NET (raw), Alimer.Bindings.WebGPU (MIT, push 2026-02-16, wraps wgpu v27.0.4.0 per its README, so about two majors behind v29). Alimer targets .NET 8/9.
- Native AOT: a flat C ABI with P/Invoke or `[LibraryImport]` is AOT-compatible by construction; bindings that use function pointers and blittable structs are the safe choice **(inferred)**.
- Unmanaged interop: WebGPU has no host-pointer import. Data enters through `wgpuQueueWriteBuffer` (copies) or mapped ranges, so a copy per upload is unavoidable; reading back requires map-async plus a staging buffer. That is a real constraint for a per-Tick Field upload pattern.
- Binding effort: medium. Reuse Alimer bindings or generate your own from `webgpu.h` (the header is small compared with Vulkan).
- macOS: yes via Metal.

## CUDA bindings

- ManagedCuda (https://github.com/kunzmi/managedCuda): wraps CUDA driver API, NVRTC, cuBLAS, etc.; Linux supported on .NET Core 3.1+; csproj targets up to net8.0. Licence: since CUDA 12 it is "dual-licensed GPLv3 / commercial", which is a blocker for a permissively-licensed Framework unless you buy a licence (README).
- Alternative: write your own P/Invoke to `libcuda`/`nvcuda.dll` (a small, stable C API) and compile kernels with NVRTC. NVIDIA-only; excludes AMD, Intel, and macOS entirely.
- CUDA offers the best unmanaged interop: `cuMemHostRegister`/pinned host memory and unified memory **(my domain knowledge, not verified in this pass)**.

## Observations for TASK-1.9

1. No single option covers Windows + Linux + macOS with compute, unmanaged-memory interop and Native AOT. wgpu-native is the closest on coverage; ILGPU v2 on authoring ergonomics; Vulkan (via Silk.NET or own bindings) on zero-copy.
2. NVRHI is attractive technically (compute, D3D12 + Vulkan, MIT, very active) but has the highest cost: you own a C shim plus bindings, and it has no macOS path. It only makes sense if the Framework wants D3D12 and Vulkan under one abstraction and accepts owning that native layer.
3. A Backend abstraction (per GLOSSARY) fits best with a small interface where `ComputeSharp` (Windows), `wgpu-native` (cross-platform) or `ILGPU` can each be an implementation; the interface should take explicit upload/readback rather than assume shared memory, since only Vulkan host-pointer import and CUDA registration offer true sharing.
4. Open items worth a spike in TASK-1.14: (a) can ILGPU v2 beta run under Native AOT end to end; (b) measured upload/readback cost for wgpu-native on a representative Field size; (c) whether `VK_EXT_external_memory_host` is exposed on target drivers.

## Sources

- https://github.com/NVIDIA-RTX/NVRHI (README, repo metadata)
- https://github.com/NVIDIAGameWorks/nvrhi/issues/36
- https://github.com/dotnet/Silk.NET (README, releases, issue #305, branch develop/3.0)
- https://docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_external_memory_host.html
- https://github.com/veldrid/veldrid (README, releases, src/Veldrid)
- https://github.com/m4rs-mt/ILGPU (README, releases, Src/Directory.Build.props, Src/ILGPUC/PACKAGE.md, Src/ILGPU/Runtime)
- https://github.com/Sergio0694/ComputeSharp (README, csproj, releases)
- https://github.com/gfx-rs/wgpu-native (README, releases, examples/compute)
- https://github.com/gfx-rs/wgpu (README backend table)
- https://github.com/amerkoleci/Alimer.Bindings.WebGPU
- https://github.com/kunzmi/managedCuda (README, LICENSE, csproj)
