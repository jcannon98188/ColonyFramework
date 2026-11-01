---
id: TASK-37
title: Migrations for Resource and extension layout changes
status: Done
assignee:
  - '@jason'
created_date: '2026-11-01 21:36'
updated_date: '2026-11-01 22:17'
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
- [x] #1 Persistent Resource layout changes can be migrated by registered migrations using the same versioning, chain-ordering and test conventions as Component migrations
- [x] #2 Framework extension sections (pathfinding, generation, residency, input) carry a version and support registered migrations across Framework versions
- [x] #3 Loading a save with a missing migration step or a newer version than the build fails with a clear reason naming the type or extension
- [x] #4 Tests migrate an old golden save for a changed Resource and for a changed extension and continue deterministically on 1, 2, 4 and N workers; existing golden saves stay byte-identical
- [ ] #5 Spec 7.1 and the mirrored docs updated to include Resource and extension migrations
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Merged at 9d06998. LoadOptions.Migrations gains AddResource<TFrom,TTo> and AddExtension(name, from, to, migrator); WorldExtensionDefinition.FormatVersion recorded in manifest (only when some extension is past 1, so old saves byte-identical). AC#5 partial: docs/spec/architecture.md 7.1 updated; Backlog doc-1 7.1 mirror deferred to the final spec sync. Gaps: Game-level extension layout changes still fail on load; PluginRegistration/Native ABI cannot register migrations; Colony.Testing.Tests lacks TieredCompilation/TieredPGO-off so AllocationHarnessTests.Allocations_during_warm_up_do_not_count flakes (TASK-42).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Resource and extension migrations: chained Resource layout migrations (values and queues), per-extension format versions with chained section migrations, specific failure messages, golden old-shape save loads and continues hash-identical on 1/2/4/N workers.
<!-- SECTION:FINAL_SUMMARY:END -->
