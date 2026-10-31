---
id: TASK-16
title: 'Plugins, Definitions and Colony.Abstractions'
status: In Progress
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-10-31 13:49'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-11
  - TASK-14
documentation:
  - >-
    backlog/docs/spec/doc-1 -
    Colony-Framework-architecture-spec-v1-part-1-of-2.md
  - >-
    backlog/docs/spec/doc-2 -
    Colony-Framework-architecture-spec-v1-part-2-of-2.md
type: task
ordinal: 43000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec section 8: the C# extension surface. A Plugin has a manifest (stable ID, version, required API version, dependencies, Deterministic declaration) and one Register entry point called once at World setup; registration closes before the first Tick. Load order comes from the dependency graph with Plugin ID ties; duplicates, cycles and unorderable writers fail at World build; a Plugin may replace another System only if that System is Overridable, recorded in the save manifest. Definitions are schema-bound unmanaged structs in a typed registry with interned names, loaded via a pluggable IDefinitionSource (a reference JSON source ships), patchable by later Plugins and baked into immutable blobs. Colony.Abstractions holds only the contracts Plugins compile against. Any NonDeterministic Plugin makes the World report non-deterministic.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A Plugin registers Components, Resources, Relations, Fields, Systems, Events and Input Commands through Register and the World runs them under the scheduler; registration after build fails
- [ ] #2 Dependency ordering, duplicate names, cycles and unorderable writers behave as specified with tests for each failure
- [ ] #3 An Overridable System is replaced by a later Plugin and the replacement is recorded in the save manifest; a non-Overridable one cannot be replaced
- [ ] #4 Definitions load from the JSON source, are patched by a later Plugin, bake into blobs and are looked up by interned name without allocation during Ticks
- [ ] #5 Saves record the Plugin set; loading with a missing Plugin fails by default and succeeds in the opt-in drop-unknown-Components mode
- [ ] #6 A Game can reference Colony.Core and Colony.Fields without Colony.Plugins and still run
<!-- AC:END -->
