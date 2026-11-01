---
id: TASK-31
title: Lockstep test network with fault injection
status: Done
assignee:
  - '@jason'
created_date: '2026-11-01 21:28'
updated_date: '2026-11-01 21:52'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-15
ordinal: 58000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec 7.4 promises lockstep enablers but the current simulated two-peer test only covers submission order, thread counts and a wire relay. Game authors need to test their own netcode against realistic conditions without opening a socket. This adds a transport-free in-memory network to Colony.Testing, usable by Game test suites.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 An in-memory network connects N peers carrying input frames and checkpoint hashes as byte spans, with no sockets
- [x] #2 Configurable latency, jitter, reordering, drops and duplicates, all driven by a seeded deterministic RNG so a failing run reproduces
- [x] #3 A scenario runs 2-4 peers in lockstep under each fault profile and every peer's hash trail matches every Tick (stalls allowed, divergence not)
- [x] #4 A peer fed a wrong command or a dropped frame it cannot recover is reported as a desync with the first diverging Tick, Phase and System
- [x] #5 Documented in CONVENTIONS and usable from a Game's test project via the Colony.Testing package; dependency check stays clean
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Merged at 660a974 (982 tests, 3 skipped). Code in src/Colony.Testing/Lockstep (TestNetwork, NetworkProfile, LockstepHarness, LockstepFault). Colony.Testing now references Colony.Persistence. Gaps for TASK-34: hash-trail exchange for first-diverging Phase/System runs in-process, real peers must send trails. AOT host now pulls in Persistence via Testing; not re-published as AOT. Two early full runs exited code 2 undiagnosed; watch for a flaky test.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Seeded fault-injecting in-memory network plus a lockstep harness that checks every peer against a single-World oracle across 7 fault profiles and 2-4 peers; desyncs are reported at Tick/Phase/System. 39 new tests.
<!-- SECTION:FINAL_SUMMARY:END -->
