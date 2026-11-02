---
id: TASK-42
title: 'Hand-forward fixes: access checks, AOT scenario coverage, Linux verification'
status: Done
assignee:
  - '@jason'
created_date: '2026-11-01 21:36'
updated_date: '2026-11-02 04:21'
labels:
  - ready-for-agent
  - implementation
dependencies: []
ordinal: 69000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Collected gaps from closed tickets that should be fixed or verified before release: access checking holes (writes via chunk.Column on read-only declarations undetected; Native System commands_emit cannot verify declared emits), pathfinding/work index/generation/claims scenarios not linked into the AOT host, Linux AOT and Linux C compiler helper paths never run locally, and the earlier fix-pass list (generator not emitting resource-access attributes, AllocationHarness not wired to the diagnostics allocation check, zero-allocation gates flaking under parallel runs, spec 11.1 sketch deviations). Intended to run with the code review fix pass.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Access checks detect writes through chunk columns declared read-only, or the limitation is documented as a deliberate exclusion with a test showing the boundary
- [x] #2 Native System command emits are checked against declared emits, or the gap is recorded in the ABI docs with the reason
- [x] #3 Scenarios for pathfinding, work index, generation and claims are linked into the AOT host and compared against JIT hashes
- [x] #4 Linux x64 build, AOT publish, C ABI conformance and cross-OS golden hashes are verified in CI or the gap is recorded precisely
- [x] #5 Source generator emits resource-access and enable-bit attributes; AllocationHarness feeds the diagnostics allocation check; zero-allocation gates stop flaking in the parallel run
- [x] #6 Spec 11.1 sketch compiles or the docs list its exact deviations
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Added from TASK-37 report: Colony.Testing.Tests lacks TieredCompilation=false/TieredPGO=false (as Core.Tests has), so AllocationHarnessTests.Allocations_during_warm_up_do_not_count flakes about 2 in 5 full runs; also TASK-35: AllocationTests 100k loop, StandardLevelOverheadTests, Pathfinding FlowFieldScenario/PathQuery allocation tests flake under load; TASK-35: inputs-restricted missing from the AOT host; TASK-33: ReplayPlayer ordering fix and PeerIdentity.CurrentFrameworkVersion tracking VersionPrefix.

From TASK-40 report: zero-alloc flakes under whole-solution load: ReachableTests.The_unreachable_target_scenario_allocates_nothing_in_steady_state(1 worker), StreamingScenarioTests.A_steady_state_tick_with_a_settled_focus_allocates_nothing(1 worker), one unnamed Pathfinding test; diagnose under load with the process-wide counter theory (parallel tests) and fix or serialize. Also JoinPackage should accept a PrepareWorld hook so streamed worlds with host-supplied ChunkSource can join.

From TASK-43/36: LoopbackLockstepTests multi-process test (samples/HelloLockstep.Tests) flakes on integration/v1 (about 1 of 3 full runs); make it robust (replace fixed sleeps, longer timeouts) or exclude from the whole-solution parallel run. Zero-alloc gates flake in Core (Hierarchy cascade delete, 100k loop), Pathfinding, CounterScenario, ColonistScenario 4 workers: candidates TieredCompilation=false in Pathfinding and Testing test projects, or serialising alloc tests. Optional: skip empty colours in in-place RunParallel.

Merged at f2af1a5 together with the code-review fix pass (findings 1-12). Not done (optional): C# context.Commands.Emit not checked against WritesResources; ReplayPlayer per-Tick ordering; PeerIdentity version tracking; JoinPackage PrepareWorld hook; skip empty colours. AC#4: Linux job exists in CI but has never run; gap recorded in ci.yml header; first CI run on the PR will verify.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Access checks tightened, native emits checked, scenarios in AOT host compared to JIT, generator emits resource-access attributes, flaky gates fixed, spec 11.1 deviations listed; plus review fixes: flow Field determinism, claim resolver generations, hash padding, atomic saves, replay header, entity generations in hash, native handle ownership, ADR 0005.
<!-- SECTION:FINAL_SUMMARY:END -->
