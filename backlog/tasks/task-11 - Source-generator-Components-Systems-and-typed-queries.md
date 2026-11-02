---
id: TASK-11
title: 'Source generator: Components, Systems and typed queries'
status: Done
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-10-31 07:55'
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
- [x] #1 A [Component] partial struct registers with a stable name and layout hash; a [System(Phase, Every)] method with ref parameters produces a System entry with the correct access set and runs under the scheduler
- [x] #2 Generated typed queries iterate at chunk level and expose per-Entity ref access on top
- [x] #3 Generator snapshot tests cover Components, Systems with reads and writes, rate attributes and ordering constraints, and fail on output drift
- [x] #4 A sample using the hello-colony shape minus Fields and Save compiles, runs 1000 Ticks and prints a World hash
- [x] #5 Access declaration mismatches (a System touching an undeclared Component) are reported at compile time where detectable
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Generator emits for [Component] partial structs: IComponent<T> implementation, field-level ComponentLayout (TASK-7 base hash then per-field name/type/offset/size via Unsafe.ByteOffset, no reflection), equality members (solves CA1815; CA1051 is an .editorconfig suppression documented on ComponentAttribute). For [System] methods: a SystemDefinition with access set, Phase, Every/Offset, After/Before, SkipAsleep, With/Without/ChangeFilter, a typed {Method}Query exposing Span/ReadOnlySpan per column plus ref XAt(row), and a generated ColonySystems.AddSystems(builder) registration. Spec 11.1 adapted: attributes take NamedPhase enum or a Phase name string, and registration is ColonySystems.AddSystems(builder) because a static class cannot be a type argument. Diagnostics COL2001-COL2007 (shape, by-value Component params, Component shape/fields, undeclared Component access via typed accessors incl. lambdas, access attribute type, rate). Snapshot tests under tests/Colony.Generators.Tests/Snapshots with COLONY_ACCEPT_SNAPSHOTS=1 to accept; runtime tests in tests/Colony.Generated.Tests incl. hello-colony (100 colonists, 1000 Ticks, hash 0xD59C35444981016B equal on 1 and 4 workers). Core additions are additive (IComponent<T>, ComponentLayout, WorldBuilder.AddComponent<T>/PhaseNamed, attributes, Fixed32.FromRatio). Gaps: writes through chunk.Column<T> on Reads-only columns are not detectable; fixed-size buffers (COL2004) and generic Components unsupported; serial Systems map to Begin only; order between conflicting generated Systems is not inferred. Orchestrator note: one transient test failure appeared in the first post-merge Release run and did not reproduce; watch for flakiness.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Source generator merged to integration/v1 (10b57f6): attributed Components and Systems become registered Components with field-level layout hashes, typed chunk queries and scheduler-ready System definitions; seven compile-time diagnostics; snapshot tests with drift detection; the hello-colony sample compiles, runs 1000 Ticks and prints a World hash identical across worker counts. Solution 474 passed, 1 skipped (AOT host).
<!-- SECTION:FINAL_SUMMARY:END -->
