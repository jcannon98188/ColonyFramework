---
id: TASK-12
title: 'Field chunks, stencils and the World change feed'
status: Done
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-10-31 07:45'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-7
documentation:
  - >-
    backlog/docs/spec/doc-1 -
    Colony-Framework-architecture-spec-v1-part-1-of-2.md
  - >-
    backlog/docs/spec/doc-2 -
    Colony-Framework-architecture-spec-v1-part-2-of-2.md
type: task
ordinal: 39000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec section 5.1 and 5.2: a Field is a named grid of one unmanaged cell struct with stable name and layout hash, stored in 16x16x16 chunks (row-major cells, SIMD along x) iterated and saved in Morton order; uniform chunks hold one value with no allocation; Fields sit behind an accessor and interface so storage can change later (the GPU seam, ADR 0003). Stencils are double-buffered by default with an in-place ordered mode using fixed chunk colouring, both deterministic regardless of thread count. Per-chunk per-Field change versions plus a World change feed of changed chunks per Tick are what region upkeep, the work index and renderers subscribe to. World dimensions are bounded and fixed at creation; depth 1 means 2D.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Fields with a bounded World size are registered through the builder, chunked at 16x16x16 and iterated in Morton order; uniform chunks allocate no cell storage until written
- [x] #2 A double-buffered stencil and an in-place coloured stencil both produce identical results across 1 and N threads in the determinism harness
- [x] #3 Grid Entities carry TilePos (3 ints) with optional fixed-point sub-tile offset and no floats
- [x] #4 The change feed lists changed chunks per Tick in deterministic order and per-chunk change versions advance only on writes
- [x] #5 Field chunks are included in the World hash in Morton order with cells in storage order
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
New package src/Colony.Fields with tests/Colony.Fields.Tests. Core gained a generic extension hook (WorldExtensionDefinition/IWorldExtension with Freeze/Create/BeginTick/EndPhase/EndTick/Hash/LayoutHash, World.Extension<T>(), World.RunParallel(count, IIndexedJob)); no Field code in Core; existing hashes unchanged without extensions. Fields: WorldSize (max 4096x4096x512), ChunkLayout (Morton ordinals + 8-colour parity tables), abstract FieldStorage seam with CpuFieldStorage oracle (64-byte aligned native dense chunks on first write, uniform = value table), FieldAccessor<T>, FieldSet (versions, LastTickChanges, ChangedSince, ResidencyOf always Active), Stencil.DoubleBuffered/InPlace as SystemDefinitions run from Begin, TilePos/SubTileOffset. Version = CurrentTick+1 at write; uniform and dense chunks with equal cells hash identically. Both stencil modes hash identically on 1/2/4/N threads over a 30-Tick diffusion + 3D Life scenario with zero allocation. Follow-ups: stencils densify visited chunks (a translation-invariance opt-in could keep uniform regions free); 2D Worlds waste 15/16 of each chunk (note for TASK-26); stencil Systems in one wave run serially on the Tick thread; ChunkProvider left to TASK-25, ChunkResidency seam in place.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Field chunks merged to integration/v1 (a7d0aee): 16^3 chunks in Morton order, uniform chunks without allocation, Field accessor over a storage seam, TilePos/SubTileOffset, per-chunk change versions and a deterministic World change feed, double-buffered and in-place coloured stencils identical across thread counts, Fields in the World hash in the specified order. 23 Fields tests plus 5 Core extension-hook tests; solution all green (Core 290, Fields 23, ProcGen 79, Generators 27, DependencyCheck 20).
<!-- SECTION:FINAL_SUMMARY:END -->
