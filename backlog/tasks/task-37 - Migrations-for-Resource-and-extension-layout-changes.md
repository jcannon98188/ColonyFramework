---
id: TASK-37
title: Migrations for Resource and extension layout changes
status: In Progress
assignee:
  - '@jason'
created_date: '2026-11-01 21:36'
updated_date: '2026-11-01 21:58'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-14
  - TASK-16
ordinal: 64000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec 7.1 only mandates Component migrations, so a Game that changes a persistent Resource or a Framework extension layout (pathfinding, generation, residency, inputs) cannot load its older saves. Long-lived games need every persisted shape to be migratable. Found while closing the save/load and Plugin tickets.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Persistent Resource layout changes can be migrated by registered migrations using the same versioning, chain-ordering and test conventions as Component migrations
- [ ] #2 Framework extension sections (pathfinding, generation, residency, input) carry a version and support registered migrations across Framework versions
- [ ] #3 Loading a save with a missing migration step or a newer version than the build fails with a clear reason naming the type or extension
- [ ] #4 Tests migrate an old golden save for a changed Resource and for a changed extension and continue deterministically on 1, 2, 4 and N workers; existing golden saves stay byte-identical
- [ ] #5 Spec 7.1 and the mirrored docs updated to include Resource and extension migrations
<!-- AC:END -->
