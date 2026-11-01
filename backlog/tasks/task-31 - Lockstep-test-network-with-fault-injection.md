---
id: TASK-31
title: Lockstep test network with fault injection
status: To Do
assignee: []
created_date: '2026-11-01 21:28'
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
- [ ] #1 An in-memory network connects N peers carrying input frames and checkpoint hashes as byte spans, with no sockets
- [ ] #2 Configurable latency, jitter, reordering, drops and duplicates, all driven by a seeded deterministic RNG so a failing run reproduces
- [ ] #3 A scenario runs 2-4 peers in lockstep under each fault profile and every peer's hash trail matches every Tick (stalls allowed, divergence not)
- [ ] #4 A peer fed a wrong command or a dropped frame it cannot recover is reported as a desync with the first diverging Tick, Phase and System
- [ ] #5 Documented in CONVENTIONS and usable from a Game's test project via the Colony.Testing package; dependency check stays clean
<!-- AC:END -->
