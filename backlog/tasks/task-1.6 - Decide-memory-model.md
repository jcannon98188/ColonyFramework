---
id: TASK-1.6
title: Decide memory model
status: Done
assignee:
  - '@jason'
created_date: '2026-10-30 13:46'
updated_date: '2026-10-31 01:38'
labels:
  - 'wayfinder:grilling'
dependencies:
  - TASK-1.3
parent_task_id: TASK-1
ordinal: 7000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Question: What is the memory strategy: native allocators and arenas, chunk size and alignment (SIMD/GPU friendly), handle scheme, side tables for managed data, pooling, lifetime/ownership rules, where unsafe is allowed, and the no-GC-allocation-per-Tick guarantee. Informed by the .NET 11 survey.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Decided with the user (all seven proposals accepted, plus the float-policy deferral):

1. Chunks: 64 KiB, 64-byte aligned columns, each column padded to a fixed multiple of 16 elements independent of CPU vector width; capacity per chunk computed from the archetype. Layout never depends on the CPU.
2. Allocation: World reserves large native slabs (~4 MiB via AlignedAlloc) carved into chunks with a free list; per-Tick scratch bump arena (reset each Tick) for command buffers and temporary query data; configurable World memory budget; allocation only at sync points, failure raises one clear exception there.
3. Safety: release builds unchecked; debug builds validate every ref/Span against the chunk structural version, poison freed memory, report leaks/double frees on World.Dispose. Rule "a ref is valid until the next structural change" documented prominently.
4. Buffer Components: inline up to author-declared capacity; overflow to a World-owned size-class allocator addressed by a handle in the Component; contents saved, handle not.
5. Shared data: World-owned interned string table (32-bit StringId, deterministic assignment order) and immutable blob store (BlobRef<T>) for shared definitions; both saved with the World; Plugins register content through them.
6. GC: zero managed allocation per steady-state Tick enforced by a test harness (GC.GetAllocatedBytesForCurrentThread) plus an analyzer for Framework code; optional NoGC guard for Game authors; docs recommend Workstation non-concurrent GC with DATAS off.
7. Unsafe: confined to Framework core internals; public API exposes only ref, Span<T> and handles; stable unsafe rules only, no C# 15 preview memory-safety rules until GA.

Deferred to Decide determinism contract: SIMD float policy (vector width, FMA, reduction order, saturating float-to-int conversions). Passed to Decide World, Field and spatial indexing design and Decide GPU Backend: Field/GPU buffer sharing with the slab allocator.

**Amendment (CPU-only v1):** the Field/GPU buffer sharing with the slab allocator is out of v1. Added from later decisions: a Field memory budget (pathfinding Field LRU) and a resident-chunk budget (streaming eviction) join the World budgets.
<!-- SECTION:FINAL_SUMMARY:END -->
