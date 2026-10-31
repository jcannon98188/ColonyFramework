---
id: TASK-11
title: 'Source generator: Components, Systems and typed queries'
status: In Progress
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-10-31 03:51'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-7
  - TASK-5
documentation:
  - >-
    backlog/docs/spec/doc-1 -
    Colony-Framework-architecture-spec-v1-part-1-of-2.md
  - >-
    backlog/docs/spec/doc-2 -
    Colony-Framework-architecture-spec-v1-part-2-of-2.md
type: task
ordinal: 38000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec sections 4.3 and 11: Systems and Components are attributed partial structs and methods, and a Roslyn incremental generator emits the static System entry, the declared access set, ordering metadata and typed queries so there is no virtual dispatch or reflection in the hot path. Generated code lives in the user assembly and uses only public surface, which is what keeps the public API safe while internals stay unsafe. The hello-colony sketch in spec section 11.1 is the target shape (Fields and Save arrive in later tickets). Snapshot tests of generated output are the compile-time test layer.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A [Component] partial struct registers with a stable name and layout hash; a [System(Phase, Every)] method with ref parameters produces a System entry with the correct access set and runs under the scheduler
- [ ] #2 Generated typed queries iterate at chunk level and expose per-Entity ref access on top
- [ ] #3 Generator snapshot tests cover Components, Systems with reads and writes, rate attributes and ordering constraints, and fail on output drift
- [ ] #4 A sample using the hello-colony shape minus Fields and Save compiles, runs 1000 Ticks and prints a World hash
- [ ] #5 Access declaration mismatches (a System touching an undeclared Component) are reported at compile time where detectable
<!-- AC:END -->
