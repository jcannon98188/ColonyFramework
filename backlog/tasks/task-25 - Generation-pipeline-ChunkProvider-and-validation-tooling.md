---
id: TASK-25
title: 'Generation pipeline, ChunkProvider and validation tooling'
status: To Do
assignee: []
created_date: '2026-10-31 01:56'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-12
  - TASK-16
  - TASK-24
documentation:
  - >-
    backlog/docs/spec/doc-1 -
    Colony-Framework-architecture-spec-v1-part-1-of-2.md
  - >-
    backlog/docs/spec/doc-2 -
    Colony-Framework-architecture-spec-v1-part-2-of-2.md
type: task
ordinal: 52000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec section 9.3 (decided in the procedural generation ticket): named stages declaring the Fields they read and write, ordered by before/after name, registered by Plugins and replaceable when Overridable; RNG keyed by (seed, stage name hash, chunk or region coordinate, use index); cross-chunk features via region-keyed placement, halo reads over neighbour base data only, and a deterministic world-level pre-pass cached as a generated Resource; Entities from placement stages with IDs derived from (seed, stage, coordinate, index). The ChunkProvider contract generates any chunk alone, in any order, on any thread, with the same bytes, which streaming relies on. Validation tooling (golden hashes per stage and seed, order-independence and seam checks, connectivity validators, per-stage timing) ships. The manifest records a generation version.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A pipeline of stages with declared Fields produces a chunk from (seed, coordinate) with identical bytes regardless of generation order or thread
- [ ] #2 Adding a stage does not change the random numbers drawn by other stages
- [ ] #3 Region-keyed placement and halo reads produce seamless features across chunk boundaries, verified by the seam check
- [ ] #4 The pre-pass Resource is deterministic and cached; placed Entities have IDs derived from (seed, stage, coordinate, index)
- [ ] #5 Validation tooling reports golden hash drift, order dependence and seam mismatches per stage, with per-stage timing
- [ ] #6 The save manifest records the generation version and regeneration is refused on mismatch
<!-- AC:END -->
