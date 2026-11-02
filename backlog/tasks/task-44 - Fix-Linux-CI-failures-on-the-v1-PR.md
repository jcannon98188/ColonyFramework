---
id: TASK-44
title: Fix Linux CI failures on the v1 PR
status: In Progress
assignee:
  - '@jason'
created_date: '2026-11-02 04:36'
updated_date: '2026-11-02 05:12'
labels:
  - ready-for-agent
  - implementation
dependencies: []
ordinal: 71000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
First CI run on PR #1 (https://github.com/jcannon98188/ColonyFramework/pull/1) failed on ubuntu-latest x64 JIT (Windows passed). Three failures: (1) Colony.Fields.Tests.FieldHashTests.A_cell_write_changes_the_world_hash_and_equal_fields_hash_equal: two Worlds given identical writes hash differently on Linux (likely non-zeroed memory or padding bytes in dense Field chunks being hashed); (2) HelloLockstep.Tests crashes with exit 134: ObjectDisposedException from TcpMesh.Outbox.Wake() called by TcpMesh.Dispose() after the outbox thread already disposed its SemaphoreSlim; (3) Colony.Testing.Tests.StreamingScenarioTests.A_slow_chunk_source_stalls_Ticks_but_produces_the_hashes_of_a_fast_one: LoadStalls = 0 with LoadsInFlight = 72, a wall-clock dependent assertion. Log: scratchpad ci-linux-failure.log.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Dense Field chunk and other framework-owned memory that is hashed is zeroed or padding-masked so equal Worlds hash equal regardless of prior memory contents, proven by a test that poisons freshly allocated memory
- [x] #2 TcpMesh.Dispose is race-free and the HelloLockstep tests cannot crash the test host
- [x] #3 The slow chunk source test asserts stalls deterministically (not on wall-clock timing) or is otherwise robust on a slow runner
- [ ] #4 dotnet test Colony.slnx -c Release passes locally and the ubuntu-latest job passes on the PR
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Merged at 0a2d0c1. AC4 (Linux job green) pending PR CI; not verifiable locally. Padding bug class also exists outside Fields (Core Components/Resources, ProcGen HashChunk, Diagnostics WorldDiff) - see follow-up.
<!-- SECTION:NOTES:END -->
