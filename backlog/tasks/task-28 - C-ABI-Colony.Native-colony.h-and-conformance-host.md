---
id: TASK-28
title: 'C ABI: Colony.Native, colony.h and conformance host'
status: To Do
assignee: []
created_date: '2026-10-31 01:56'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-16
  - TASK-15
documentation:
  - >-
    backlog/docs/spec/doc-1 -
    Colony-Framework-architecture-spec-v1-part-1-of-2.md
  - >-
    backlog/docs/spec/doc-2 -
    Colony-Framework-architecture-spec-v1-part-2-of-2.md
type: task
ordinal: 55000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec section 8 and 10.3: the second of the two extension surfaces. A narrow versioned function table loaded in-process (host opt-in) exposing schema introspection, read-only chunk views valid within a Tick, command-buffer submission as the only write path, RNG streams, events, Definition lookups and Input Command submission; Native Plugin Systems run under the same scheduler with access declared in data. Colony.Native is an AOT shared library and colony.h is generated and checked in so changes are visible in review. A C host that registers a Native Plugin and exercises every entry on JIT and AOT is the conformance suite. Two Native AOT runtimes in one process is an open risk to document.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A C host loads the Framework, registers a Native Plugin System with declared access, submits commands and Input Commands, reads chunk views and Definitions, and runs Ticks on both JIT and Native AOT
- [ ] #2 Every entry in the function table is exercised by the conformance suite and the C ABI version is recorded in manifests and saves
- [ ] #3 colony.h is generated from the ABI definition, checked in, and a check fails when the generated header differs from the committed one
- [ ] #4 A Native Plugin System participates in the determinism harness and hashes identically to its C# equivalent
<!-- AC:END -->
