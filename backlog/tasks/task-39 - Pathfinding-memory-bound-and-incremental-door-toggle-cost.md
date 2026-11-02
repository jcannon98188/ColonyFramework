---
id: TASK-39
title: Pathfinding memory bound and incremental door-toggle cost
status: Done
assignee:
  - '@jason'
created_date: '2026-11-01 21:36'
updated_date: '2026-11-02 02:03'
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
- [x] #1 ChunkGraph memory is bounded by a configurable budget (LRU or lazy build) and counted in the diagnostics record; behaviour past the budget is defined and tested
- [x] #2 A union-find merge fast path and a bounded split search make door-toggle cost proportional to the change, with before and after numbers on the small and big benchmark Worlds
- [x] #3 The randomized oracle tests for regions and flow repair still match, including 3D with stairs, on 1 and 4 workers
- [x] #4 Determinism goldens unchanged, zero-allocation gates still pass, benchmarks updated
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Merged at 529079d. ChunkGraph shared LRU pool (ChunkGraphMemoryBudgetBytes, 64 MiB default, floor 4 chunks), ~8.3 KB/chunk vs ~25 KB; regions are union-find slots with canonical ids so incremental equals rebuild; door toggle small 508->118 us, big 4.6 ms->124 us. Original diagnosis partly wrong: TASK-19 numbers mostly measured re-checking every chunk because host terrain writes stay dirty until a Tick passes. Gaps: that dirty-until-Tick behaviour belongs with TASK-40 (host-write change-stamping); Reachable 37/73 ns has no old baseline; compaction runs not a counter.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Pathfinding chunk graph memory is bounded by an LRU budget and door toggles update regions incrementally with ids identical to a full rebuild; goldens unchanged, steady-state allocation-free.
<!-- SECTION:FINAL_SUMMARY:END -->
