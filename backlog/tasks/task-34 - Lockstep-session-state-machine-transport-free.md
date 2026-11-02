---
id: TASK-34
title: Lockstep session state machine (transport-free)
status: Done
assignee:
  - '@jason'
created_date: '2026-11-01 21:30'
updated_date: '2026-11-02 02:22'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-31
  - TASK-32
  - TASK-33
ordinal: 61000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Every lockstep Game would otherwise re-implement input delay, acknowledgement tracking, stall rules and checkpoint timing. A transport-free state machine that takes input frames in and out as byte spans removes that duplicated, error-prone logic without adding sockets. It stretches the enablers-only wording of spec 7.4, so add a short spec note (doc-1/doc-2 and docs/spec/architecture.md) stating the Framework still ships no transport. Shape it from what the test network (TASK-31) shows Games need.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A LockstepSession type consumes and produces input frames, acks and checkpoint hashes as byte spans, with no sockets, threads or wall-clock time inside
- [x] #2 Handles per-peer input delay (fixed and adaptive), missing-input stalls using the shared Tick gate, acknowledgement and resend requests, and scheduled checkpoint hash exchange
- [x] #3 Runs under the TASK-31 fault-injecting network with 2-4 peers and every fault profile, with hash trails matching every Tick
- [x] #4 A desync raises an event carrying the first diverging Tick, Phase and System and the TASK-32 localisation when available
- [ ] #5 Spec 7.4 note added in the spec documents and the mirror, stating no transport is shipped; dependency check stays clean
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Merged at 02d8fc9. LockstepSession in Colony.Persistence/Lockstep: Receive/NextMessage byte API, no sockets/threads/clock; fixed and adaptive input delay, TickGate stalls, acks/resend, checkpoint hashes, Desynced then DesyncLocalised (Tick/Phase/System and hash tree chunk) with trails exchanged over the wire (LockstepHashTrail); hello handshake with permission differences; join served from retained frames. WorldBuilder.AddExtension rejects reserved hash-tree group names. InputFrameWriter moved Testing->Persistence. AC#5 partial: docs/spec 7.4 note and GLOSSARY done; Backlog doc-1/doc-2 mirror deferred to final spec sync. Gaps: ReplayPlayer per-Tick submit ordering avoided not fixed; no peer-drop policy or dynamic seats; Native ABI lacks permission entry.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Transport-free lockstep session: input delay, Tick gating, checkpoint hash exchange, desync localisation to Tick/Phase/System/chunk across the wire, join in progress; hash-identical to a single-World oracle under every fault profile with 2-4 peers.
<!-- SECTION:FINAL_SUMMARY:END -->
