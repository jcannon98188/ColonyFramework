---
id: TASK-19
title: 'Pathfinding: movement classes, region IDs and Reachable'
status: To Do
assignee: []
created_date: '2026-10-31 01:56'
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
- [ ] #1 Movement classes are registered as Definitions with passability rules; structures for a class are built on first use and the per-World limit and ceiling are enforced at build
- [ ] #2 Region IDs update incrementally from the change feed; a merge, a split and a mass change each produce the same labels as a from-scratch rebuild in tests
- [ ] #3 Reachable(from, to, movementClass) answers in constant time and the unreachable-target scenario is added to the determinism harness
- [ ] #4 Region data is excluded from saves and rebuilt on load, with a benchmark reporting rebuild time for the prototype world sizes
<!-- AC:END -->
