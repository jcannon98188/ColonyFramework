---
id: TASK-10
title: 'Colony.Testing: determinism and allocation harnesses'
status: In Progress
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-10-31 07:55'
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
ordinal: 37000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec section 10.3 and 6: hashes prove sameness, so the Framework ships a determinism harness (as a package Plugins and Games can run) that executes seeded scenarios across 1/2/4/N threads, ISA settings and Native AOT, compares committed golden hashes across OSes, and includes a negative control that must fail. The allocation harness checks every thread for managed allocations in steady-state Ticks. The spec warns that hashes prove sameness not correctness, so model-based tests stay separate. Later tickets (persistence, replay, streaming, pathfinding) add scenarios to this harness rather than inventing their own.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A scenario is defined by seed plus parameters; the harness runs it across configured thread counts and compares World hashes, reporting the first diverging Tick
- [ ] #2 Golden hashes are committed per scenario and compared on every run; a deliberately non-deterministic negative-control scenario fails
- [ ] #3 The allocation harness fails a scenario if any Framework thread allocates during steady-state Ticks and names the System running at the time
- [ ] #4 The harness runs under Native AOT with IlcInstructionSet x86-64-v3 and reports the same hashes as JIT
- [ ] #5 Debug validation switches (ref checks, poisoning, leak checks) are exercised by at least one failing-on-purpose test each
<!-- AC:END -->
