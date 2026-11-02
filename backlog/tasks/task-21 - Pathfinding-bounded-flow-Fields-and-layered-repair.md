---
id: TASK-21
title: 'Pathfinding: bounded flow Fields and layered repair'
status: Done
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-11-01 21:26'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-20
documentation:
  - >-
    backlog/docs/spec/doc-1 -
    Colony-Framework-architecture-spec-v1-part-1-of-2.md
  - >-
    backlog/docs/spec/doc-2 -
    Colony-Framework-architecture-spec-v1-part-2-of-2.md
type: task
ordinal: 48000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec section 9.1: shared goals use bounded flow Fields (goal chunks plus a radius) built lazily under an LRU cap within the pathfinding Field memory budget; units outside the bound use HPA* to reach the edge. Repair is layered: per-chunk dirty marking, lazy repair on read, a per-Tick work budget with a staleness bound, and an up-front rebuild-versus-incremental threshold, because the prototype found CPU incremental repair can cost more than a rebuild. Full-World Fields broke at 512x512x64 in the prototype, which is why Fields are bounded.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A flow Field for a goal set is built lazily, bounded by radius, evicted by LRU under the configured memory budget, and units outside the bound route to its edge with HPA*
- [x] #2 Dirty chunks repair lazily within the per-Tick budget; a staleness bound forces repair; the rebuild-versus-incremental threshold is configurable and measured by a benchmark
- [x] #3 Flow Field results are identical across thread counts and excluded from saves
- [x] #4 Pathfinding counters (request queue depth, work units, repair backlog, Field memory versus budget) appear in the diagnostics records
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Flow Fields: lazy, radius-bounded, LRU under a memory budget; layered incremental repair with staleness bound and mass-change rebuild; not hashed or saved; counters in the Tick record. Real 3D repair bug caught by randomized oracle test and fixed. Follow-ups: ChunkGraph ~18 KB/chunk still unbounded; TASK-19 door-toggle union-find fast path not done; flow BFS does not special-case Unloaded chunks (TASK-26); distances ushort; upward repair join conservative.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Flow Fields with memory-budgeted LRU, layered incremental repair, determinism goldens and diagnostics counters merged to integration/v1.
<!-- SECTION:FINAL_SUMMARY:END -->
