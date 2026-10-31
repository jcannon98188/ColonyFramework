---
id: TASK-17
title: 'Diagnostics core: record ring, logging, allocation check, hash trail'
status: To Do
assignee: []
created_date: '2026-10-31 01:56'
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
ordinal: 44000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec section 10.1 (decided in the profiling and diagnostics ticket): always-on numeric counters in a preallocated per-Tick record ring with a versioned layout, cost levels Off/Standard/Detailed with Standard under about 1% of Tick time, an allocation-free in-process read API (world.Diagnostics), a first-class all-thread allocation check, an optional Tick budget with slow-Tick reports, structured allocation-free logging (message IDs plus numeric args drained between Ticks, no wall-clock time), flag-controlled validation switches and an optional hash trail per Tick/Phase/System/archetype. Core never references Diagnostics; it exposes hooks through interfaces and struct callbacks. The ECS prototype showed per-System timing finds hotspots immediately while per-job timing is itself measurable overhead, hence the levels.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 world.Diagnostics returns the latest N Tick records as structs or spans with Tick, Phase, per-System time, chunks visited and skipped, worker busy/spin/park and memory versus budget, without allocating
- [ ] #2 Standard level overhead is measured by a benchmark and stays under about 1% of Tick time on the prototype colonist scenario; Off records only Tick time and the allocation check
- [ ] #3 Structured logging from Systems allocates nothing, carries Tick and System ID, and resolves text only when drained
- [ ] #4 The allocation check reports the System running when a managed allocation occurred, per thread
- [ ] #5 With the hash trail enabled, two runs that diverge report the first differing Tick, Phase and System
- [ ] #6 Record layout is versioned and Core has no reference to Colony.Diagnostics (verified by the dependency check)
<!-- AC:END -->
