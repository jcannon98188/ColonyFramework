---
id: TASK-8
title: 'Resources, typed events, enable bits and System rates'
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
ordinal: 35000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec sections 4.1 and 4.3: Resources are World-owned singleton unmanaged structs in a keyed store with declared access, saved with the World; events are typed queues held as Resources, written through per-System buffers merged in System order and cleared at a stated point, with no callbacks; frequent state flips use enable bits rather than add/remove tags; Systems run every N Ticks with an optional offset defaulting from the System ID and skip chunks via change versions or an asleep bit. These complete the data model Systems rely on before Fields and persistence are built on top.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Resources are registered with declared access, participate in scheduler conflict detection and are included in the World hash
- [ ] #2 Typed event queues written from parallel Systems are observed in System order then record order, cleared at the documented point, and are deterministic across thread counts
- [ ] #3 Enable bits per Component exist, iteration respects them, and flipping a bit causes no structural change
- [ ] #4 Every-N-Ticks with offset runs Systems on the expected Ticks; chunks skipped by change version or asleep bit are counted and never visited
- [ ] #5 All of the above pass the determinism harness scenario across thread counts with zero steady-state allocation
<!-- AC:END -->
