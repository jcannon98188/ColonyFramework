---
id: TASK-1.15
title: Prototype GPU pathfinding for many units
status: Done
assignee:
  - '@jason'
created_date: '2026-10-30 23:15'
updated_date: '2026-10-31 01:27'
labels:
  - 'wayfinder:prototype'
dependencies: []
parent_task_id: TASK-1
ordinal: 16000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Question: For many units (10k-100k) on a 3D tile World, does GPU pathfinding (parallel wavefront / BFS flow fields shared per goal, plus batched per-unit queries) beat a well-designed CPU approach (hierarchical A* with region/connected-component IDs, CPU flow fields) in total cost per Tick including upload/readback and dynamic obstacle updates? Throwaway prototype; link it. Execution: this prototype must be built by an Opus 5.5 agent (model claude-opus-5-5). Builds on the GPU Field prototype bindings and findings in docs/research/prototype-gpu-field.md.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Decided with the user (all proposals accepted; Q2 revised to drop GPU from v1). Report: docs/research/prototype-gpu-pathfinding.md. Big-World hybrid and thread-scaling rows were not run (the big-World flow-Field and HPA* rows already break the budget).

**Findings:** moving 100k units on the GPU costs 0.11 ms, so repairing Fields after obstacle changes is the whole cost. GPU shared-goal flow Fields win only about 2x (7-9 ms vs 12-17 ms at 100k units, 64 goals, 0.001% toggles) and neither design holds 16 ms at p99 under toggles. GPU per-unit queries lose about 17-20x to CPU HPA*. CPU HPA* is the best unique-goal planner but 3000 requests per Tick still cost 160-200 ms (small World). CPU region IDs are mandatory (40-110 ns per request; unreachable A* otherwise costs 42-437 ms). GPU and CPU distance Fields and moves are bit-identical. 512x512x64 full-World Fields break both Backends (2.15 GB per 64 Fields; repair 38.6 ms GPU, 156 ms CPU at 0.001% toggles).

**GPU not in v1:** the measured GPU gain (15x only for dense GPU-authoritative Fields, about 2x for pathfinding) does not justify the bindings, second Kernel implementation, authority model, device-loss handling and test matrix before a real workload needs it. v1 is CPU-only. Kept cheap seams: Fields stay behind a Field interface with the CPU implementation as the oracle, Kernel/Backend vocabulary stays in the glossary, and CPU Fields use the integer-distance and u16 formulation a GPU package would reuse. GPU becomes a separate later package; the GPU prototypes stay in docs/research and prototypes/ as its starting point. This supersedes the GPU Backend decision (compute-only Backend, wgpu-native, Exact/Approximate Kernels, authority per Field) as a v1 deliverable, and the GPU-specific parts of the testing (Q9), profiling (Q9) and determinism decisions.

**Pathfinding architecture (default):** always-on CPU region IDs; chunk graph with HPA* for unique goals with a per-Tick request budget and queue; CPU flow Fields for shared goals; plain A* only for short local refinement; no GPU per-unit queries. Provides the three queries the jobs decision needs (Reachable by movement class, approximate cost, budgeted deferred path request).

**Caveats:** rooms-and-doors terrain, uniform random toggles (worst case for chunk dirtying), one Vulkan card, single-level HPA*, CPU repair varied about 40% between runs. Treat numbers as orders of magnitude.

**Handed forward:** pathfinding-at-scale ticket (bounded/hierarchical Fields, second HPA* level, time-sliced or lazy repair, clustered toggles) and streaming worlds, blocked on it.
<!-- SECTION:FINAL_SUMMARY:END -->
