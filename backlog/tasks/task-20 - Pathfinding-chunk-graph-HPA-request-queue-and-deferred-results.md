---
id: TASK-20
title: 'Pathfinding: chunk-graph HPA*, request queue and deferred results'
status: Done
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-11-01 16:57'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-19
documentation:
  - >-
    backlog/docs/spec/doc-1 -
    Colony-Framework-architecture-spec-v1-part-1-of-2.md
  - >-
    backlog/docs/spec/doc-2 -
    Colony-Framework-architecture-spec-v1-part-2-of-2.md
type: task
ordinal: 47000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec section 9.1: unique-goal paths use two-level chunk-graph HPA* (approximate within a few percent of optimal) refined near the unit by local A*; per-unit GPU queries lost 17-20x to CPU HPA* in the prototype so this is the main path. Requests go through a canonical queue processed in (priority class, request ID) order under a budget counted in work units, with results delivered N Ticks later in the Begin Phase; a missed deadline fails with a deterministic event and a request through an Unloaded chunk fails with needs-loading carrying the chunk coordinate. Games own unit movement; a path-follow helper ships.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 HPA* paths for unique goals are within a documented few percent of optimal on test maps and are bit-identical across thread counts
- [x] #2 Path requests submitted in Tick T are answered in Tick T+N in the Begin Phase in canonical order under a work-unit budget; budget exhaustion defers deterministically and deadline misses emit the specified event
- [x] #3 Approximate path cost and the next-step-toward-goal query are available and allocation-free
- [x] #4 The path-follow helper moves a Grid Entity along a result with fixed-point sub-tile offsets
- [x] #5 Toggle and budget-exhaustion scenarios are added to the determinism harness
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
HPA* over chunk graph (paths mean 1.02x optimal vs BFS oracle, worst 1.17x), deferred request queue (budget 200k work units/Tick, ResultDelayTicks 2, priority-then-id order, DeadlineMissed), PathQuery cost/next-step, PathFollow helper. Pending queue hashed and saved. TASK-19 pathfinding-unreachable golden re-accepted (schema change: two Systems now registered; per-System RNG seeding). Follow-ups: one non-reproducible allocation flake (7976 B) in a full-solution run; ChunkGraph reserves ~18 KB per built chunk (~280 MB on 512x512x256 World) so TASK-21 should bound it; residency read via FieldSet.ResidencyOf, always Active until TASK-26 wires real residency.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Chunk-graph HPA*, deterministic request queue with deferred results, cost/next-step queries and path-follow helper merged to integration/v1.
<!-- SECTION:FINAL_SUMMARY:END -->
