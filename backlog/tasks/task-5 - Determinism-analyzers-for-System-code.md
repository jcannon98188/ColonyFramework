---
id: TASK-5
title: Determinism analyzers for System code
status: To Do
assignee: []
created_date: '2026-10-31 01:56'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-3
documentation:
  - >-
    backlog/docs/spec/doc-1 -
    Colony-Framework-architecture-spec-v1-part-1-of-2.md
  - >-
    backlog/docs/spec/doc-2 -
    Colony-Framework-architecture-spec-v1-part-2-of-2.md
type: task
ordinal: 32000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec section 6 lists APIs that silently break Tier 1 determinism inside Systems: Math and MathF transcendentals, Vector<T>, DateTime and Stopwatch, Guid.NewGuid, reference-type GetHashCode, unordered managed collections and System.Random. Catching these at compile time is far cheaper than finding them with the determinism harness, and the analyzers do not depend on the source generator, so they can ship from the Colony.Generators scaffold early and protect every later ticket. Rule: all orderings end with an Entity ID tiebreak.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Each banned API listed in spec section 6 produces an analyzer diagnostic when used inside code marked as System code, and no diagnostic outside it
- [ ] #2 Analyzer diagnostic tests cover a positive and a negative case per rule
- [ ] #3 Diagnostics are errors by default through the shared build props and can be suppressed per site with a documented justification attribute or pragma
- [ ] #4 The analyzers ship in the Colony.Generators package and load into a consuming project without the generator being present
<!-- AC:END -->
