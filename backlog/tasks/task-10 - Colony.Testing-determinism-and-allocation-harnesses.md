---
id: TASK-10
title: 'Colony.Testing: determinism and allocation harnesses'
status: Done
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-10-31 12:38'
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
- [x] #1 A scenario is defined by seed plus parameters; the harness runs it across configured thread counts and compares World hashes, reporting the first diverging Tick
- [x] #2 Golden hashes are committed per scenario and compared on every run; a deliberately non-deterministic negative-control scenario fails
- [ ] #3 The allocation harness fails a scenario if any Framework thread allocates during steady-state Ticks and names the System running at the time
- [x] #4 The harness runs under Native AOT with IlcInstructionSet x86-64-v3 and reports the same hashes as JIT
- [x] #5 Debug validation switches (ref checks, poisoning, leak checks) are exercised by at least one failing-on-purpose test each
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Merged into integration/v1 at 39fbd78 (2026-10-31). Full solution Release green twice (564 total, 0 failed; the Colony.Testing AOT comparison skips only when the host is not published). New package src/Colony.Testing: Scenario, DeterminismHarness/Result/Options, GoldenHashStore, AllocationHarness/Result/Options, HarnessHost, PublishedHost, HarnessException; tests/Colony.Testing.Tests (39) and tests/Colony.Testing.AotHost (replaces tests/Colony.Core.AotHost; publish with dotnet publish tests/Colony.Testing.AotHost -c Release -r win-x64, IlcInstructionSet x86-64-v3). Goldens committed at tests/Colony.Testing.Tests/Golden/{colonist,fields}.golden, compared every run; COLONY_ACCEPT_GOLDEN=1 rewrites them. Goldens identical in Debug/Release/DOTNET_EnableAVX2=0 and under Native AOT. Negative control (thread-keyed RNG) fails the comparison as required. Colonist oracle is a single source in tests/Colony.Core.Tests/Scenario/ColonistScenario.cs with Compose/Populate; Testing.Tests wraps it; Core.Tests ColonistScenarioTests retired into Testing.Tests. Core change: InternalsVisibleTo Colony.Testing.Tests only (debug scenarios read slab/scratch poison bytes). AC #3 left unchecked: the harness fails on any main/worker allocation and names the Phase/wave and its Systems (exact when a System is alone in its wave, listed together otherwise); exact per-System naming should be wired to the TASK-17 allocation check (AllocationReport / WorkerRecord.FirstAllocatingSystem, which landed in parallel) - scheduled for the code-review fix pass. Other follow-ups: docs/testing.md not written (publish command documented in the AotHost csproj and CONVENTIONS); CI should publish the AOT host before dotnet test so AC #4 stays enforced (TASK-30/CI follow-up).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Colony.Testing: determinism harness (identical hashes across 1/2/4/N workers, first diverging Tick, faults/leaks fail), golden hash store with accept flag, allocation harness (fails on any Framework-thread allocation in steady-state Ticks, names the wave's Systems), Native AOT host comparison (ran, identical hashes), and debug-validation scenarios (stale views, use-after-free poison, leak reports). Merged at 39fbd78; AC #3 exact per-System attribution deferred to the fix pass.
<!-- SECTION:FINAL_SUMMARY:END -->
