---
id: TASK-19
title: 'Pathfinding: movement classes, region IDs and Reachable'
status: Done
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-10-31 21:22'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-12
  - TASK-16
documentation:
  - >-
    backlog/docs/spec/doc-1 -
    Colony-Framework-architecture-spec-v1-part-1-of-2.md
  - >-
    backlog/docs/spec/doc-2 -
    Colony-Framework-architecture-spec-v1-part-2-of-2.md
type: task
ordinal: 46000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec section 9.1 (decided in the pathfinding at scale ticket, evidence in the GPU pathfinding prototype report): region IDs (connected components per movement class) are mandatory because they make unreachable requests O(1), roughly 40-110 ns in the prototype, while plain A* was infeasible at scale. Regions are maintained incrementally from the change feed: merges are cheap, splits trigger a bounded local relabel with a chunk-set rebuild fallback. Movement classes are Game-defined Definitions each with its own passability rule, built lazily per class on first use, with a per-World default limit of 32 and a hard ceiling of 256. Caches are not saved. Tier 1 deterministic.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Movement classes are registered as Definitions with passability rules; structures for a class are built on first use and the per-World limit and ceiling are enforced at build
- [x] #2 Region IDs update incrementally from the change feed; a merge, a split and a mass change each produce the same labels as a from-scratch rebuild in tests
- [x] #3 Reachable(from, to, movementClass) answers in constant time and the unreachable-target scenario is added to the determinism harness
- [x] #4 Region data is excluded from saves and rebuilt on load, with a benchmark reporting rebuild time for the prototype world sizes
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
New package Colony.Pathfinding (Core+Fields). RegionId is long. Region upkeep at Late System, BeginTick and Sync; Late Systems writing dependent Fields must order Before PathfindingBuilder.SystemName. Save stores only built-class bitmask; regions rebuilt on load, never hashed. Reachable 33-70 ns, 0 B. Rebuild small 40 ms (1 worker)/6.6 ms (8); big 329/56 ms. Follow-ups: incremental door toggle costs 12 ms small/95 ms big on 1 worker (union-find merge fast path, bounded split search); unreachable scenario not linked into AOT host; class first built mid-Tick builds serially under lock. Merge run hit the known Hierarchy zero-alloc flake under load; two reruns passed.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Pathfinding movement classes, region IDs with incremental upkeep, Reachable queries and region persistence merged to integration/v1.
<!-- SECTION:FINAL_SUMMARY:END -->
