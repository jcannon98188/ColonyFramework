---
id: TASK-12
title: 'Field chunks, stencils and the World change feed'
status: In Progress
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-10-31 03:51'
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
- [ ] #1 Fields with a bounded World size are registered through the builder, chunked at 16x16x16 and iterated in Morton order; uniform chunks allocate no cell storage until written
- [ ] #2 A double-buffered stencil and an in-place coloured stencil both produce identical results across 1 and N threads in the determinism harness
- [ ] #3 Grid Entities carry TilePos (3 ints) with optional fixed-point sub-tile offset and no floats
- [ ] #4 The change feed lists changed chunks per Tick in deterministic order and per-chunk change versions advance only on writes
- [ ] #5 Field chunks are included in the World hash in Morton order with cells in storage order
<!-- AC:END -->
