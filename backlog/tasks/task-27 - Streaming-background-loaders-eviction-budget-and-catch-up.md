---
id: TASK-27
title: 'Streaming: background loaders, eviction budget and catch-up'
status: In Progress
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-11-01 17:04'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-26
  - TASK-19
documentation:
  - >-
    backlog/docs/spec/doc-1 -
    Colony-Framework-architecture-spec-v1-part-1-of-2.md
  - >-
    backlog/docs/spec/doc-2 -
    Colony-Framework-architecture-spec-v1-part-2-of-2.md
type: task
ordinal: 54000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec section 5.3: loads run on background workers with a bounded queue and apply after a fixed configurable N-Tick delay; a load not ready by its deadline stalls the Tick and is reported by diagnostics; Games may submit prefetch hints. A resident-chunk budget drives deterministic eviction (farthest from focus first, ties by chunk coordinate, Hibernating before Active) with a budget-pressure event. Catch-up is frozen by default; Systems opt in with a deterministic function of (state, elapsed Ticks), run under a work budget with a cap on replayed Ticks. Unloaded chunks keep a small pathfinding summary (portals, per-class region labels) so Reachable can answer across them.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Loads complete on background workers and apply exactly N Ticks after request; a late load stalls the Tick and the stall is counted in diagnostics
- [ ] #2 Eviction under the resident-chunk budget follows the specified order and emits the budget-pressure event; results are identical across thread counts
- [ ] #3 A System with opt-in catch-up produces the same state as if it had run every Tick, within the replayed-Tick cap, under the work budget
- [ ] #4 Unloaded chunks retain a pathfinding summary and Reachable answers across them; a path request into an Unloaded chunk fails with needs-loading and the chunk coordinate
- [ ] #5 Load-deadline stall scenarios are added to the determinism harness and streaming counters appear in the diagnostics records
<!-- AC:END -->
