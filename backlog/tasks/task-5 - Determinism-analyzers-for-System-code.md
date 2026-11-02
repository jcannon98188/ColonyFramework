---
id: TASK-5
title: Determinism analyzers for System code
status: Done
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-10-31 02:52'
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
- [x] #1 Each banned API listed in spec section 6 produces an analyzer diagnostic when used inside code marked as System code, and no diagnostic outside it
- [x] #2 Analyzer diagnostic tests cover a positive and a negative case per rule
- [x] #3 Diagnostics are errors by default through the shared build props and can be suppressed per site with a documented justification attribute or pragma
- [x] #4 The analyzers ship in the Colony.Generators package and load into a consuming project without the generator being present
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Single DeterminismAnalyzer in Colony.Generators with seven descriptors COL1001-COL1007 (transcendentals; Vector<T>; DateTime/Stopwatch/TickCount; Guid.NewGuid; reference GetHashCode; enumeration of unordered collections; System.Random). Scope is lexically inside a [Colony.System] type or method; [Colony.AllowNonDeterministic("why")] or a pragma opts out. Marker attributes live in Colony.Core (SystemAttribute, AllowNonDeterministicAttribute) and are matched by fully qualified name so the analyzer needs no Core reference; TASK-11 reuses SystemAttribute. In-house analyzer test harness (no Microsoft.CodeAnalysis.Testing dependency). Known non-flagged escapes: enumeration through interface-typed views, inferred lambda parameter types, HashCode.Add on foreign HashCode; tighten if TASK-10 finds leaks. NuGet consumers get Error severity from descriptors only; no props file shipped yet.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Determinism analyzers merged to integration/v1 (f1864f6). Seven rules with positive and negative tests each (27 tests in Colony.Generators.Tests), errors by default and pinned in WarningsAsErrors, suppression attribute and pragma covered by tests, analyzer loads from Colony.Generators without the generator present (verified with an external scratch project). Whole solution: 48/48 tests pass, dependency check clean.
<!-- SECTION:FINAL_SUMMARY:END -->
