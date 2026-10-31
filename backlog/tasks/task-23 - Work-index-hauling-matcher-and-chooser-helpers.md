---
id: TASK-23
title: 'Work index, hauling matcher and chooser helpers'
status: To Do
assignee: []
created_date: '2026-10-31 01:56'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-22
  - TASK-13
  - TASK-19
documentation:
  - >-
    backlog/docs/spec/doc-1 -
    Colony-Framework-architecture-spec-v1-part-1-of-2.md
  - >-
    backlog/docs/spec/doc-2 -
    Colony-Framework-architecture-spec-v1-part-2-of-2.md
type: task
ordinal: 50000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec section 9.2: a per-work-type, per-chunk work index of available work items with deterministic ordering and change tracking, queried by radius-bounded bucket search (nearest N available of types), rebuilt on load. An optional hauling matcher pairs supply and demand into haul work items. Optional lexicographic priority-table chooser and utility scoring helpers ship; no behaviour trees or GOAP in v1. Reachability from the pathfinding service filters candidates.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Nearest N available work items of given types within a radius return in deterministic order and respect Reachable for the requesting movement class
- [ ] #2 The work index updates in the Late Phase from the change feed and is rebuilt on load (not saved)
- [ ] #3 The hauling matcher produces haul work items from supply and demand deterministically
- [ ] #4 Priority-table and utility helpers are allocation-free and covered by unit tests
<!-- AC:END -->
