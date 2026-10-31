---
id: TASK-1.9
title: Decide GPU Backend abstraction and first Backend
status: Done
assignee:
  - '@jason'
created_date: '2026-10-30 13:46'
updated_date: '2026-10-31 01:38'
labels:
  - 'wayfinder:grilling'
dependencies:
  - TASK-1.2
parent_task_id: TASK-1
ordinal: 10000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Question: What does the Backend abstraction expose (buffers, compute dispatch, sync with the CPU Tick), and which concrete Backend ships first (NVRHI via bindings, Silk.NET, Veldrid, own thin layer, other)? Windows and Linux required, macOS nice to have. Informed by the GPU survey.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
**SUPERSEDED FOR v1 (TASK-1.15 and the GPU-in-v1 decision): v1 is CPU-only and ships no GPU Backend.** The measured GPU gain (about 15x only for dense GPU-authoritative Fields, about 2x for shared-goal pathfinding) did not justify the bindings, second Kernel implementation, authority model, device-loss handling and test matrix before a real workload needs it. What v1 keeps from this ticket: Fields behind a Field interface with the CPU implementation as the reference and oracle; the Kernel/Backend vocabulary in the glossary; integer-distance and fixed-point formulations a GPU package could reuse; the Exact/Approximate classification as a rule for any future GPU Kernel. The text below is retained unchanged as the starting design for a later, separate `Colony.Gpu.*` package (prototype findings in docs/research/prototype-gpu-field.md and prototype-gpu-pathfinding.md amend it: mirror pattern dropped, CPU default with explicit per-Field GPU opt-in, transfer budgets, staging ring plus scatter Kernel, Vulkan default, Exact-only Kernels in deterministic Worlds, own generated bindings with AOT in CI).

---

Decided with the user (all ten proposals accepted; NVRHI dropped).

**Abstraction (Q1):** compute-only Backend interface: Device, Buffer, Kernel (compiled pipeline), Command batch, Fence, capability set. Backends are separate packages behind it, so Game authors/Plugins can supply others. No rendering API.

**Kernels (Q2):** one shader language compiled at build time and shipped as data (no runtime compiler); WGSL for v1. Every GPU kernel also has a CPU SIMD implementation that is the reference, test oracle and fallback.

**First Backend (Q3):** wgpu-native (DX12/Vulkan/Metal; Windows, Linux, macOS; flat C ABI suits Native AOT). NVRHI dropped: no macOS, no C API, no C# bindings. Nothing in the abstraction blocks a later NVRHI/raw-Vulkan Backend if measurements justify it. WebGPU/wasm is not a target; wgpu-native is used purely as a native cross-API layer.

**Bindings (Q4):** own thin bindings generated from webgpu.h/wgpu.h using [LibraryImport] and blittable structs, pinned to one wgpu-native version; native binaries in per-platform runtime packages. Prototype may check whether reusing Alimer's bindings saves time.

**Transfer (Q5):** explicit upload/readback of dirty chunks as batches via a staging ring. Capabilities (HostPointerImport, UnifiedMemory) let a future Backend opt into zero-copy without changing Systems. GPU side: per-Field chunk pool plus chunk index table; uniform chunks use no GPU memory.

**Timing (Q6):** deferred completion with fixed per-Field latency in Ticks (default 1). Submit in Tick T, apply at T+k. If the GPU is late the Tick waits on the fence and a profiler warning fires. Never apply whatever happens to be ready.

**Determinism classes (Q7):** each kernel is Exact (integer/fixed-point, bit-identical to CPU reference on every Backend, included in World hashes/replays) or Approximate (float; excluded from hashes; output Fields flagged non-deterministic). Determinism ticket defines the guarantees for saves/replays.

**CPU fallback (Q8):** CPU Backend always available and runs the reference kernels; GPU use is opt-in per Field. Exact kernels are tested CPU-vs-GPU on every shipped Backend.

**Device selection/loss (Q9):** prefer discrete adapter, host-overridable; init failure falls back to CPU with a clear log. On device loss, GPU mirrors of CPU-authoritative Fields are rebuilt from CPU memory; GPU-authoritative Fields need a periodic readback checkpoint (configurable interval) or the loss is reported unrecoverable.

**Renderer sharing (Q10):** v1 renderer reads Field data from CPU memory via the change feed (GPU-authoritative Fields after readback). Native GPU handle export is a later optional capability outside the Backend core.

**Handed forward:** Determinism ticket sets Exact/Approximate guarantees and SIMD float rules. GPU Field prototype should spike: wgpu-native upload/readback cost on dirty chunks, fence/deferred-completion behaviour, own-vs-Alimer bindings, Native AOT with the P/Invoke layer, and WGSL kernel + CPU reference parity.
<!-- SECTION:FINAL_SUMMARY:END -->
