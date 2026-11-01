---
id: TASK-42
title: 'Hand-forward fixes: access checks, AOT scenario coverage, Linux verification'
status: To Do
assignee: []
created_date: '2026-11-01 21:36'
updated_date: '2026-11-01 22:17'
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
- [ ] #1 Access checks detect writes through chunk columns declared read-only, or the limitation is documented as a deliberate exclusion with a test showing the boundary
- [ ] #2 Native System command emits are checked against declared emits, or the gap is recorded in the ABI docs with the reason
- [ ] #3 Scenarios for pathfinding, work index, generation and claims are linked into the AOT host and compared against JIT hashes
- [ ] #4 Linux x64 build, AOT publish, C ABI conformance and cross-OS golden hashes are verified in CI or the gap is recorded precisely
- [ ] #5 Source generator emits resource-access and enable-bit attributes; AllocationHarness feeds the diagnostics allocation check; zero-allocation gates stop flaking in the parallel run
- [ ] #6 Spec 11.1 sketch compiles or the docs list its exact deviations
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Added from TASK-37 report: Colony.Testing.Tests lacks TieredCompilation=false/TieredPGO=false (as Core.Tests has), so AllocationHarnessTests.Allocations_during_warm_up_do_not_count flakes about 2 in 5 full runs; also TASK-35: AllocationTests 100k loop, StandardLevelOverheadTests, Pathfinding FlowFieldScenario/PathQuery allocation tests flake under load; TASK-35: inputs-restricted missing from the AOT host; TASK-33: ReplayPlayer ordering fix and PeerIdentity.CurrentFrameworkVersion tracking VersionPrefix.
<!-- SECTION:NOTES:END -->
