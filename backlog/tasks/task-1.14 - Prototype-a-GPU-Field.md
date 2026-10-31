---
id: TASK-1.14
title: Prototype a GPU Field
status: Done
assignee:
  - '@jason'
created_date: '2026-10-30 13:46'
updated_date: '2026-10-31 01:38'
labels:
  - 'wayfinder:prototype'
dependencies:
  - TASK-1.8
  - TASK-1.9
parent_task_id: TASK-1
ordinal: 15000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Question: Does a GPU-accelerated Field (for example heat diffusion on a 512x512x256 World) beat the SIMD CPU version on the chosen Backend, including upload/readback costs against the Tick budget? Throwaway prototype; link it.

Execution: this prototype must be built by an Opus 5.5 agent (model claude-opus-5-5).
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Report in `docs/research/prototype-gpu-field.md`; code in `prototypes/gpu-field/` (throwaway, never imported by production code; seeds for the Backend parity suite). Reviewed with the user; all nine proposals accepted.

**Results (heat diffusion, 512x512x256 dense World, RTX 2060 SUPER, PCIe Gen3 x8, wgpu-native v29.0.1.1):** GPU-authoritative wins about 15x: Kernel 2.07 ms (Vulkan) / 2.31 ms (DX12) against 31.9 to 34 ms on the best CPU path (AVX2, 16 threads), 2.2 to 2.5 ms end to end; reading back 1% of chunks adds about 2.7 to 2.9 ms. CPU-authoritative with a GPU mirror never wins on a dense World (70 to 108 ms; about 6 GB/s effective PCIe). Break-even about 30% of chunks moved per Tick; fits a 16 ms Tick only at about 15% or less. Small (256x256x64) and sparse (5% of chunks) Worlds: CPU already 1 to 2 ms, GPU not needed. Integer Kernel bit-identical CPU vs GPU on Vulkan and DX12 up to 500 steps; float Kernel differs up to 5 ULP. DX12 much slower than Vulkan for submit-heavy and writeBuffer-heavy work. Native AOT works with hand-written LibraryImport bindings; default AOT drops AVX2 unless IlcInstructionSet is set. Default WebGPU limits fail the full World; adapter limits are required. Caveats: a dense stencil every Tick is a worst case (real Fields use active sets and slower cadences); one machine, one GPU vendor, random dirty-chunk patterns.

**Revisions adopted:**
1. **Mirror pattern dropped (Q1):** Field modes are CPU-only or GPU-authoritative. The CPU reads back only dirty regions or regions of interest and sends edits up as a compact list. Device-loss recovery for GPU-authoritative Fields is checkpoint-based on the save schedule. Amends the GPU Backend decision's CPU-mirror recovery.
2. **Per-Field choice (Q2):** CPU is the default; GPU is an explicit per-Field opt-in by the Game author, guided by docs (worth considering above about 5 Mi active cells, stencil-style update, small readback fraction). No automatic switching in v1.
3. **Transfer budgeting (Q3):** document the budget (about 15% of a 256 MiB Field per Tick); spread full snapshots with a rolling chunk cursor or asynchronous k>=2 completion; diagnostics add a transfer-over-budget warning.
4. **Upload and submit practices (Q4):** mapped staging ring (not writeBuffer for large or numerous writes), GPU scatter Kernel for dirty chunks, batch many steps per submit, Vulkan as the default wgpu backend on Windows/NVIDIA with DX12 as fallback.
5. **Limits (Q5):** request adapter limits at device creation and record them in Capabilities; pool paging across bindings is a documented fallback and not built in v1; Fields too large for the adapter are rejected with a clear error at World build.
6. **Determinism (Q6):** deterministic Worlds may run only Exact (integer or fixed-point) Kernels on a GPU; float Kernels are Approximate and reading their Fields marks the World non-deterministic. The prototype's verify mode seeds the Backend parity suite.
7. **Extras (Q7):** no design around zero-copy (keep the HostPointerImport capability flag only); use parallel memcpy for pack and scatter; non-temporal stores for the CPU path noted as an untried later optimisation.
8. **Bindings (Q8):** generate our own bindings from the wgpu-native header (output checked in) with Native AOT as a CI target; AOT packaging sets IlcInstructionSet.
9. **Closure (Q9):** this ticket closes with these revisions; the GPU Backend line on the map is amended. The GPU-in-v1 decision remains open until the pathfinding prototype reports and is then settled in one pass. Current lean: the Backend abstraction stays in the v1 spec, with GPU shipping as an optional package behind it rather than a v1 requirement.

**Status after the GPU-in-v1 decision:** GPU is not in v1 (CPU-only). The amendments above to the GPU Backend decision apply only to a later optional GPU package; this prototype's results are retained as that package's starting evidence.
<!-- SECTION:FINAL_SUMMARY:END -->
