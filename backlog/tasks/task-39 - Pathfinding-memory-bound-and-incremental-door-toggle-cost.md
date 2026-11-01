---
id: TASK-39
title: Pathfinding memory bound and incremental door-toggle cost
status: In Progress
assignee:
  - '@jason'
created_date: '2026-11-01 21:36'
updated_date: '2026-11-01 21:53'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-19
  - TASK-20
  - TASK-21
ordinal: 66000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Two limits were left by the pathfinding tickets: the ChunkGraph reserves about 18 KB per built chunk with no bound (about 280 MB on a 512x512x256 World), and toggling one door costs 11.8 ms on a small map and 95 ms on a big one with one worker because region relabelling covers most of a connected colony. Spec 9.1 sets no budget, so choose and document budgets.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 ChunkGraph memory is bounded by a configurable budget (LRU or lazy build) and counted in the diagnostics record; behaviour past the budget is defined and tested
- [ ] #2 A union-find merge fast path and a bounded split search make door-toggle cost proportional to the change, with before and after numbers on the small and big benchmark Worlds
- [ ] #3 The randomized oracle tests for regions and flow repair still match, including 3D with stairs, on 1 and 4 workers
- [ ] #4 Determinism goldens unchanged, zero-allocation gates still pass, benchmarks updated
<!-- AC:END -->
