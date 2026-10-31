# Prototype: GPU-accelerated Field on wgpu-native

Status: throwaway prototype, measured 2026-10-30. Code: `prototypes/gpu-field/`. Raw output: `prototypes/gpu-field/results/`.

Question: does a GPU-accelerated **Field** (heat diffusion, 6-neighbour stencil, 512x512x256 World) beat the SIMD CPU version on wgpu-native, including upload/readback, inside a 16 ms **Tick**?

## Summary verdict

**Yes, but only when the GPU is authoritative for the Field.**

- **GPU-authoritative (state lives on the GPU, CPU reads back only small dirty regions): it wins by about 15x.** On the full dense World, the GPU **Kernel** takes 2.07 ms (Vulkan) / 2.31 ms (DX12). The best CPU path (AVX2, 16 threads) takes 31.9–34 ms. End-to-end per Tick, including the fence wait, is 2.2 ms (Vulkan) / 2.5 ms (DX12). Reading back 1% of chunks every Tick costs about 2.7–2.9 ms. Comfortably inside 16 ms; the CPU alone is not.
- **CPU-authoritative with a GPU mirror (upload dirty chunks, step, read everything back): it never wins on a dense World.** It costs 70–108 ms per Tick, because about 6 GB/s of effective PCIe bandwidth makes moving 256 MiB each way cost about 65 ms. That is worse than the CPU alone (32 ms).
- **Break-even.** On the full World, the GPU path wins as long as per-Tick traffic (up + down) stays below about 30% of the World. It fits 16 ms only when traffic stays at or below about 15% of chunks (about 38 MiB at about 2.8 GiB/s for scattered chunks, plus about 2 ms for the step).
- **Small (256x256x64) and sparse (5% of chunks) Worlds: the GPU is not needed.** The CPU already takes 1–2 ms. GPU-authoritative takes 0.1–0.16 ms. The CPU-authoritative mirror (3.8–6 ms) loses to the plain CPU.
- **Determinism holds.** The integer Kernel is **bit-identical** between the CPU and the GPU on both Vulkan and DX12, up to 500 steps. The float Kernel is not: it differs by up to 5 ULP, most likely from FMA contraction on the GPU.
- **Native AOT works** with hand-written `[LibraryImport]` bindings and no warnings. One trap: by default AOT does not enable AVX2 for `Vector256` (see Surprises).

## Method

- **Bindings.** Hand-written `[LibraryImport]` bindings against wgpu-native **v29.0.1.1**.
  - Source: official `gfx-rs/wgpu-native` release, asset `wgpu-windows-x86_64-msvc-release.zip`, commit `6aed5095…`, SHA256 `7E67D744…F78132`; `wgpuGetVersion` returns `0x1D000101`.
  - Structs are blittable. Callbacks use `delegate* unmanaged` with `[UnmanagedCallersOnly]`, and userdata lives in `NativeMemory`.
  - Files: `Interop/Wgpu.cs`, `native/wgpu-v29.0.1.1/`.
- **Backend abstraction** (sketch, `Backend/Abstractions.cs`):
  - `IComputeDevice` with `IGpuBuffer`, `IKernel`, `IBindingSet`, `ICommandBatch` (Dispatch, Copy), `IFence` (IsComplete/Wait).
  - A `Capabilities` record carrying buffer limits, timestamp support, `HostPointerImport` (false on wgpu) and `IntegerBitExact`.
  - The wgpu implementation is in `Backend/WgpuDevice.cs`. The fence is `wgpuQueueSubmitForIndex` + `wgpuDevicePoll(wait, &idx)`. GPU time comes from timestamp queries through `timestampWrites` (1 ns period on both backends).
- **Field layout** (`Fields/ChunkedField.cs`):
  - Chunks are 16x16x16, row-major (x fastest), held in a slot pool and found through a chunk table (`SlotOf`, where -1 means a uniform chunk with one value).
  - The pool is double-buffered (Cur/Next), with 64 B of padding so SIMD can do ±1 loads unconditionally.
  - Uniform chunks act as fixed-value boundaries. World edges clamp to self.
- **Kernels** (`Kernels/Diffusion.cs`, `Kernels/GpuDiffusion.cs`):
  - *Exact*: i32, `c + ((sum6 - 6c) >> 3)`. Integer-only, so results should be bit-reproducible.
  - *Approx*: f32, `c + (sum6 - 6c) * 0.125`, summed in a fixed order.
  - The CPU stencil is generic over `IStencilRule<T>` (static abstracts), with scalar, `Vector128` and `Vector256` paths and `Parallel.For` over slots (8 chunks per task).
  - The GPU Kernel's WGSL is generated from the same rule. It runs one 16x16 workgroup per active chunk; each thread marches z with a rolling window, and neighbour chunks are looked up through `slotOf`/`uniformVal`. Dispatch switches to 2D above 65535 chunks.
- **Transfers.**
  - A staging ring (3 x 4096 chunks = 64 MiB per ring entry) for upload (MapWrite) and download (MapRead).
  - Copies of adjacent slots are merged into runs. For comparison, `queue.writeBuffer` was also measured both per chunk and for the whole pool.
- **Worlds.**
  - *full*: 512x512x256 dense (16384 chunks, 64 Mi cells, 256 MiB per buffer for i32).
  - *small*: 256x256x64 dense (1024 chunks).
  - *sparse*: 512x512x256 with 5% of chunks allocated (822 slots).
- **Measurement.** Wall time from `Stopwatch`, with warm-up before timing. Each row reports mean/p50/p99/min/max. The CPU-authoritative pattern was measured synchronously. GPU-authoritative uses deferred completion with k=1 (Tick N's step is waited on at Tick N+1).

## Results

### Correctness (`results/verify-*.txt`)

| Check | Vulkan | DX12 |
|---|---|---|
| CPU scalar / V128 / V256 / V256-MT vs scalar reference (Exact) | identical | identical |
| GPU vs CPU, Exact, small, 500 steps | bit-identical | bit-identical |
| GPU vs CPU, Exact, full dense, 20 steps | bit-identical | bit-identical |
| GPU vs CPU, Exact, sparse, 200 steps | bit-identical | bit-identical |
| GPU vs CPU, Approx f32, small, 100 steps | 30.3% cells differ, max 5 ULP, max \|d\| 1.8e-7 | 47.5% differ, max 4 ULP |

### CPU step cost, JIT (ms per step, mean; `results/cpu-jit.txt` + earlier run)

| World | Exact V256 1T | Exact V256 MT | Exact V128 MT | Exact scalar MT | Approx V256 1T | Approx V256 MT |
|---|---|---|---|---|---|---|
| full dense (64 Mi cells) | 52.8–58.6 | **31.9–34.0** | 33.0–34.6 | – | 54.8–58.9 | 31.6–35.9 |
| small dense (4 Mi cells) | 3.05–3.73 | **1.80–1.97** | 1.89–2.18 | 2.9–3.1 (p99 9.8) | 3.16–3.43 | 1.78–1.98 |
| sparse 5% (3.4 Mi cells) | 1.76–2.09 | **1.13–1.33** | 1.20–1.33 | 1.55 | 2.0–2.07 | 1.07–1.33 |

Ranges are the spread across two runs. The full World is limited by memory bandwidth: 16 threads give only 1.65x over one thread, and V128 is roughly equal to V256 when multi-threaded. Throughput is about 2.1 G cells/s.

### GPU Kernel time (timestamp queries, ms per step, mean; `results/gpu-*.txt`)

| World | Vulkan | DX12 | 1-step submit + fence round trip (Vulkan) |
|---|---|---|---|
| full dense | **2.07** (≈32.6 G cells/s, ≈260 GB/s effective) | 2.31 | 2.25 |
| small dense | 0.14 | 0.147 | 0.24 |
| sparse 5% | 0.10 | 0.097 | 0.21 |

When 20 steps are batched into one submit, throughput equals the Kernel time, so submission overhead is fully hidden.

### Transfers, full dense World (`results/xfer-*.txt`)

| Operation | Vulkan | DX12 |
|---|---|---|
| Full upload through staging ring (256 MiB) | ≈65 ms (pack memcpy 22 + PCIe copy 43) | ≈70 ms |
| Full readback (256 MiB) | ≈67 ms (copy 42.6 + scatter memcpy 24.5) | similar |
| `queue.writeBuffer` 256 MiB (+ submit) | 92 ms, very noisy (call alone 24–150) | call 41, with submit 111 |
| Reference: host memcpy 256 MiB | 21.6 ms | 21.6 ms |

Dirty chunks, scattered at random (mean ms):

| Dirty fraction | Vulkan staging up | Vulkan writeBuffer per chunk | Vulkan readback | DX12 staging up | DX12 writeBuffer per chunk | DX12 readback |
|---|---|---|---|---|---|---|
| 1% (164 chunks, 2.6 MiB) | 1.0 | 1.0 | 0.96 | 0.85 | 2.35 | 0.84 |
| 5% (819 chunks, 12.8 MiB) | 4.98 | 5.1 | 4.8 | 4.17 | 12.4 | 4.05 |
| 25% (4096 chunks, 64 MiB) | 22.2 | 30.2 | 22.0 | 19.0 | 61.3 | 20.4 |

For scattered 16 KiB chunks, effective throughput is about 2.5–3.3 GiB/s. Scattered copies are slower than one contiguous copy: the 25% case spends 16.3 ms in the GPU copy against an ideal of about 10.7 ms.

### End-to-end per Tick (`results/e2e-*.txt`, mean ms; 16 ms budget)

| Pattern | World | Vulkan | DX12 | Fits 16 ms? |
|---|---|---|---|---|
| CPU only (V256 MT) | full dense | 32–34 | 32–34 | **no** |
| GPU-authoritative, no readback | full dense | **2.19** (CPU-thread cost ≈0.05) | 2.52 | yes |
| GPU-authoritative + 1% readback every Tick | full dense | 2.67–2.89 | 2.75 | yes |
| GPU-authoritative + full readback every 60 Ticks | full dense | mean 3.2–3.8, **p99 ≈70 (spike)** | similar | mean yes, spike tick no |
| GPU-authoritative + full readback every Tick | full dense | 46–48 | 45.5 | no |
| CPU-authoritative mirror: 1% up, all back | full dense | 71–74 | 70 | no |
| CPU-authoritative mirror: 5% up, all back | full dense | 74–78 | 74 | no |
| CPU-authoritative mirror: 25% up, all back | full dense | 93–108 | 88 | no |
| CPU only | small | 1.8–2.1 | 1.8–2.1 | yes |
| GPU-authoritative | small | 0.16 | 0.16 | yes |
| CPU-authoritative mirror | small | 4.6–6.0 | similar | yes, but slower than CPU |
| CPU only | sparse 5% | 1.1–1.4 | 1.1–1.4 | yes |
| GPU-authoritative | sparse 5% | 0.10–0.11 | 0.10–0.11 | yes |
| CPU-authoritative mirror | sparse 5% | 3.8–4.9 | similar | yes, but slower than CPU |

### Break-even (full dense World)

GPU Tick cost ≈ 2.1 ms + transferred bytes / ≈2.8 GiB/s (scattered chunks). The CPU costs about 32 ms.
- **The GPU beats the CPU** while per-Tick traffic is below about 30% of chunks (≈80 MiB in total).
- **The GPU fits 16 ms** while traffic is at or below about 15% of chunks (≈38 MiB, about 2400 chunks up + down).
- **Full readback every Tick loses**: 46 ms against the CPU's 32 ms. It is the one GPU-authoritative case that is slower than the CPU.

### P/Invoke and command overhead (`results/pinvoke-*.txt`)

| Item | Vulkan | DX12 |
|---|---|---|
| `wgpuGetVersion` (normal P/Invoke) | 19–24 ns | 19–24 ns |
| same with `[SuppressGCTransition]` | 17.5–19.6 ns (so the GC transition costs ≈2–5 ns) | same |
| Record one dispatch (setPipeline/bindGroup/dispatch) | 0.4–0.5 µs | 0.4–0.5 µs |
| Finish + submit of 1000 dispatches | 6.2–6.8 ms | **15–16 ms** |
| Record one copyBufferToBuffer | 0.1–0.4 µs | 0.1–0.4 µs |
| `queue.writeBuffer` of 32 B | 1.7 µs | **15.3 µs** |
| Empty submit + fence wait | 0.097 ms | 0.011 ms |

P/Invoke cost is irrelevant at this granularity: a Tick makes tens of calls. What costs time is the driver work at submit (thousands of dispatches) and DX12's `writeBuffer`.

### Memory

| Side | Full dense World | Sparse 5% |
|---|---|---|
| Host Field (2 x pool + tables) | 513 MiB | 26.8 MiB |
| GPU (computed): 2 x pool + 3 x 64 MiB upload ring + 3 x 64 MiB download ring | 896 MiB | – |
| GPU (nvidia-smi delta) | Vulkan +839 MiB, DX12 +687 MiB | – |
| Process private bytes (full mirror run) | 830–925 MiB | – |

A GPU-authoritative design does not need the host copy of the pool. It needs only a small shadow of the regions that get read back, plus smaller staging rings.

### Native AOT (`results/cpu-aot-*.txt`)

- `dotnet publish -r win-x64 -p:PublishAot=true` succeeds with no trim/AOT warnings. Output: a 1.9 MB exe plus the 9.1 MB `wgpu_native.dll`.
- Verify is still bit-identical under AOT, and P/Invoke costs are unchanged.
- Default AOT build: `Vector256.IsHardwareAccelerated == false`. The V256 single-thread path takes 78.8 ms (JIT: 52.8). Multi-threaded is still ≈33 ms because the full World is bandwidth-bound.
- With `-p:IlcInstructionSet=x86-64-v3`, V256 is accelerated: 63 ms single-thread, 32 ms multi-threaded.

### wgpu API friction (`results/probe.txt`)

- **Buffer limits.** With default WebGPU limits (256 MiB max buffer, 128 MiB max storage binding, 8 storage buffers per stage), the full World fails validation: `Buffer binding 0 range 268435456 exceeds max_*_buffer_binding_size limit 134217728`. This happened on both backends.
  - The fix is to request the adapter's limits.
    - Vulkan: maxBufferSize 1 TiB, binding 2147483644, 524288 storage buffers per stage.
    - D3D12: maxBufferSize 2147483647, binding 2147483644.
  - Paging the pool across several bindings was not needed here, but it is the fallback for adapters with WebGPU-sized limits. For example, a 1 GiB Field (f32 at 1024x1024x256) would exceed D3D12's 2 GiB cap with double buffering if both buffers had to be in one binding.
- `writeBuffer` writes are only flushed by the *next* `queueSubmit`. A Device "wait idle" does not flush them, so naive timings under-report. The benchmark follows writes with an empty submit plus a fence wait.
- `active` is a reserved WGSL keyword (the binding was renamed `activeSlots`).
- Map callbacks need `AllowProcessEvents` plus `wgpuDevicePoll`, and the map state must live in pinned/native memory.
- Only 65535 workgroups per dimension, so Worlds with more than 65535 chunks need 2D dispatch.

## Surprises

1. **PCIe is the whole story for the mirror pattern.** About 6 GB/s effective (nvidia-smi reports the link as Gen3 x8, half of what the card supports). Moving 256 MiB each way costs more than twice the CPU step.
2. **DX12 is much slower than Vulkan for submit-heavy and writeBuffer-heavy work.** Submitting 1000 dispatches costs 2.4x more. `writeBuffer` costs 9x more for small writes and 2x more per chunk at 25% dirty. For the Kernel alone, DX12 is about 10% slower.
3. **Native AOT silently drops AVX2** unless an instruction set is specified. The code still runs and is still correct, just 50% slower single-threaded.
4. **Multi-threading barely helps the CPU on the full World** (1.65x on 16 threads). The CPU stencil saturates memory bandwidth at about 2.1 G cells/s; the GPU runs at about 32.6 G cells/s.
5. **The GPU's float results differ from the CPU's by up to 5 ULP** even with a fixed summation order, most likely because the shader compiler contracts operations into FMA. The integer Kernel matches exactly.
6. **Occasional full readbacks show up as tail spikes** (p99 ≈70 ms) even when the mean looks fine.

## Recommended design changes

1. **Make the Field Backend GPU-authoritative by default for large dense Fields.**
   - The CPU reads only dirty regions or regions of interest back.
   - The CPU sends edits up as a compact list (cell writes or dirty chunks).
   - Never mirror the whole Field on every Tick.
2. **Keep a CPU Backend as a first-class path.**
   - Small and sparse Worlds (≤ about 5 Mi active cells) already fit the budget on the CPU at 1–2 ms.
   - The GPU adds complexity and transfer latency there for no budget benefit.
   - Choose the Backend per Field from the active cell count and the expected read-back fraction.
3. **Budget transfers explicitly.**
   - A Tick may move about 15% of a 256 MiB Field before it exceeds 16 ms.
   - Spread full snapshots (saves, debug views) over many Ticks with a rolling chunk cursor, or take them asynchronously with k≥2 so they don't cause a 70 ms spike.
4. **Upload dirty chunks with a GPU scatter Kernel.**
   - Pack dirty chunks contiguously into staging and do one copy.
   - A small compute Kernel then scatters them to their slots.
   - This avoids the scattered-copy penalty (16.3 ms vs 10.7 ms at 25%).
5. **Avoid `queue.writeBuffer` for large or numerous writes, especially on DX12.** Use the mapped staging ring. Keep `writeBuffer` for small uniforms/params only.
6. **Batch many steps or Fields per submit.** Recording is cheap (≈0.5 µs per dispatch). Submit cost grows with dispatch count, especially on DX12. Prefer Vulkan as the default wgpu backend on Windows/NVIDIA.
7. **Request the adapter's limits at device creation and record them in `Capabilities`.** Design the pool so it can be split across several bindings ("pages") when `maxStorageBufferBindingSize` is small.
8. **Use integer Kernels wherever determinism matters** (lockstep, replays, CPU/GPU cross-check). Float Kernels should be treated as Backend-specific.
9. **HostPointerImport (zero-copy) does not decide this.** It would remove only the pack/scatter memcpy (≈22–25 ms of the ≈65 ms full transfer, about 1/3). PCIe transfer time stays. For the GPU-authoritative pattern, transfers are already small, so it buys almost nothing. Parallel memcpy (multi-threaded pack/scatter) gets most of the same benefit without the capability. Keep the `HostPointerImport` flag in `Capabilities`, but do not design around it.
10. **Native AOT builds must set `IlcInstructionSet`** (for example `x86-64-v3`), or ship runtime-dispatched variants. Otherwise `Vector256` paths run in emulation.
11. Possible CPU improvement, not tried: non-temporal stores for the Next buffer, since the CPU path is bandwidth-bound.

## Known shortcuts (throwaway prototype)

- In the Field, every allocated chunk is active. Inactive (uniform) chunks stay frozen; a real system would wake neighbours as heat crosses into them and would allocate or free chunks.
- One staging-ring entry per Tick. The ring does not handle transfers larger than one entry (64 MiB) in a single Tick except by blocking.
- `IFence.IsComplete` is conservative (it checks that the queue is empty, not the specific submission index).
- Only the hand-written bindings were measured. Generated bindings (for example Alimer/Silk.NET-style wrappers) were not compared.
- The CPU-authoritative mirror was measured synchronously only. Overlapping upload of Tick N+1 with the step of Tick N would hide some, but not most, of the PCIe cost.
- The CPU baseline in one Vulkan e2e run was noisy (about 40 ms). Use the dedicated `cpu` mode numbers.
- Dirty chunks were chosen uniformly at random. Real edits cluster, which would merge into longer copy runs and run faster.
- There is one CS9113 compiler warning (an unused constructor parameter in `WgpuDevice.cs`). Its only effect is the warning.
- Only one machine was measured (one NVIDIA GPU, PCIe Gen3 x8). An integrated GPU or another vendor could change the transfer conclusions a lot.

## How to rerun

From `prototypes/gpu-field/`:

```
dotnet build -c Release
dotnet bin/Release/net11.0/GpuField.dll probe            # adapters, limits, default-limits failure (exit 3 is expected)
dotnet bin/Release/net11.0/GpuField.dll verify vulkan    # or dx12
dotnet bin/Release/net11.0/GpuField.dll cpu
dotnet bin/Release/net11.0/GpuField.dll gpu vulkan       # or dx12
dotnet bin/Release/net11.0/GpuField.dll xfer vulkan
dotnet bin/Release/net11.0/GpuField.dll e2e vulkan
dotnet bin/Release/net11.0/GpuField.dll pinvoke vulkan

# Native AOT
dotnet publish -c Release -r win-x64 -p:PublishAot=true -o <outdir>
dotnet publish -c Release -r win-x64 -p:PublishAot=true -p:IlcInstructionSet=x86-64-v3 -o <outdir-v3>
<outdir>/GpuField.exe cpu
```

`wgpu_native.dll` is copied from `native/wgpu-v29.0.1.1/lib/` into the output by the csproj.

## Machine

- CPU: Intel Core i9-9900KF, 8 cores / 16 threads, AVX2 (no AVX-512).
- RAM: 64 GB.
- GPU: NVIDIA GeForce RTX 2060 SUPER, 8 GB. Driver 610.88 (Windows 32.0.16.1088). Link reported as PCIe Gen3 x8.
- OS: Windows 11 Pro 10.0.26200.
- .NET: SDK 11.0.100-rc.2.26504.105, runtime 11.0.0 (workstation, non-concurrent GC). wgpu-native v29.0.1.1.
