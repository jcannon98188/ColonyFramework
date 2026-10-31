---
id: TASK-1.2
title: Survey GPU compute options from .NET
status: Done
assignee:
  - '@research-agent'
created_date: '2026-10-30 13:46'
updated_date: '2026-10-30 13:53'
labels:
  - 'wayfinder:research'
dependencies: []
parent_task_id: TASK-1
ordinal: 3000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Question: Which GPU compute options are viable from .NET 11 on Windows and Linux (macOS a bonus): NVRHI (C++; needs native bindings), Silk.NET Vulkan/WebGPU, Veldrid, ILGPU, ComputeSharp, wgpu-native, CUDA bindings? For each: compute-shader support, buffer interop with unmanaged CPU memory, Native AOT friendliness, maintenance state, binding effort, platform coverage. Verify NVRHI facts (APIs supported, compute support, existing C# bindings).
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Findings in `docs/research/gpu-compute-options.md`.

- NVRHI: D3D11/D3D12/Vulkan 1.3, Windows+Linux, compute supported, MIT, active; no Metal/macOS, no C API (none planned), no C# bindings found, so highest binding effort (own C shim).
- wgpu-native: best coverage (DX12/Vulkan/Metal), WGSL compute, active, flat C ABI suits Native AOT; Alimer .NET bindings lag; no host-pointer import so uploads copy.
- ILGPU: pure C#, 1.5.3 stable, 2.0.0-beta1 adds MSBuild-time kernel compiler (promising for AOT, unproven); CPU/CUDA/OpenCL, Metal/ROCm on main only.
- ComputeSharp: Windows-only DX12, stale since 2025-04. Silk.NET 2.x maintenance mode (3.0 in dev); raw Vulkan gives VK_EXT_external_memory_host (zero-copy route). Veldrid dormant. ManagedCuda GPLv3/commercial, NVIDIA-only.
- Overall: no single option covers Win/Linux/macOS + compute + unmanaged-memory interop + Native AOT. Suggest Backend abstraction with explicit upload/readback.
- Spike items for the GPU prototype: ILGPU v2 under Native AOT; wgpu-native upload/readback cost per Field; VK_EXT_external_memory_host availability.
<!-- SECTION:FINAL_SUMMARY:END -->
