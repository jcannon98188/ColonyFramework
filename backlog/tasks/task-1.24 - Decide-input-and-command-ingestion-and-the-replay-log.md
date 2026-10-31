---
id: TASK-1.24
title: 'Decide input and command ingestion, and the replay log'
status: Done
assignee:
  - '@jason'
created_date: '2026-10-31 00:48'
updated_date: '2026-10-31 01:38'
labels:
  - 'wayfinder:grilling'
dependencies: []
parent_task_id: TASK-1
ordinal: 25000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Question: How does external input (player actions, UI commands, script and Plugin requests, debug commands) enter the World deterministically, and what is the replay log? Cover: the input command model (typed, schema-registered commands vs raw events; relation to the command buffer and the debug lane), stamping commands to a Tick and the host-side submission API (thread safety, ordering of same-Tick commands from multiple sources), validation and rejection (who validates, deterministic failure events), the replay log format (inputs plus seed plus Plugin set plus generation version, relation to the save container, periodic checkpoints and World hashes), recording and playback modes (record from the start, resume from a save), how replays interact with Approximate/NonDeterministic Plugins and hand-edited Worlds, input from the C ABI and script Plugins, and how the replay verifier and the inspection tooling's replay-check consume it. Inputs: determinism contract, save/load container, scheduler (Phases, host-owned loop), Plugin/C ABI decision, inspection tooling, testing strategy.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Decided with the user (all twelve proposals accepted; Q12 extended at the user's request: support networking as much as possible without implementing it). Builds on the determinism contract, save container, scheduler Phases and host-owned loop, Plugin/C ABI decision, inspection tooling (debug lane) and testing strategy.

**Input Command (Q1):** one concept: a typed, schema-registered unmanaged struct (stable name and layout hash) registered by a Plugin in Register. The Framework defines the envelope and ingestion path; Game authors define commands. Plain data, so recordable, replayable, inspectable and passable through the C ABI.

**Submission (Q2):** world.Submit(command) from any thread; stamped with its effective Tick (next Tick boundary by default, or an explicit later Tick). Nothing changes the World mid-Tick; the wall clock is never read inside the simulation.

**Ordering (Q3):** at each Tick boundary a canonical input list is built, sorted by (effective Tick, source ID, per-source sequence number). Source IDs are small stable IDs registered by the host (players, script Plugins, the debug lane). Arrival order never matters; the sorted list is what the replay log records.

**Application (Q4):** a dedicated Input Phase at the start of each Tick. Each command type has a registered handler System reading the sorted commands and writing through normal command buffers. Handlers validate and decide outcomes; Plugins register handlers and may override them if Overridable.

**Validation (Q5):** rejection is deterministic inside the handler (World state decides) and yields an ordinary CommandRejected{source, seq, reason} event. Rejected commands stay in the replay log so playback reproduces rejections. Structural validation (unknown type, bad layout) fails at Submit and never enters the log.

**Replay log (Q6):** header (Framework and format versions, Plugin set and versions, generation version, world seed or starting-save reference, tick rate); canonical input list per non-empty Tick; periodic checkpoint World hashes (configurable interval); optional full-save checkpoints for seeking. No outputs recorded: replay is start state plus inputs, divergence detected by hash.

**Format (Q7):** same sectioned container as saves (manifest, schema table for command types, chunked input sections with checksums), sharing tooling, compression and schema machinery. Starting state is a seed or an embedded/referenced save; saves during recording can be tagged as seek points.

**Modes (Q8):** record from the start or from any Tick boundary (via checkpoint save). Playback loads the start state, feeds inputs at recorded Ticks and optionally verifies checkpoint hashes. Supports headless fast-forward, seek via nearest save checkpoint, and fork to live input; live input during playback is rejected unless the host forks.

**Non-determinism (Q9):** bit-exact replay is guaranteed only for fully Deterministic Plugin sets without Approximate GPU Field reads; the header records this and the verifier reports "not guaranteed replayable" instead of a false failure. Debug-lane edits are ordinary recorded commands from the debug source and flagged in the manifest.

**Randomness (Q10):** handlers use counter-based RNG keyed by (command source, sequence number, per-use index); no wall-clock, thread IDs or system entropy, enforced by analyzers.

**C ABI and scripts (Q11):** Native and script Plugins submit through the same Submit entry (command type handle plus layout-checked payload) and register handler Systems through usual registration; a script Plugin is just another source ID; command schemas are discoverable through schema introspection.

**Networking (Q12, extended):** the Framework implements no networking (no transport, relay, NAT traversal, lobbies, prediction or rollback), but the design is built to support Game authors' lockstep networking:
- the canonical input list and Input Command envelope have a stable serialisable wire form, so peers can exchange per-Tick input frames in the same format the replay log uses;
- remote players are source IDs; handlers receive the source ID so they can reject commands that target Entities the source does not own;
- explicit future effective Ticks support input delay (the standard lockstep latency hide);
- a Tick gate on the host loop (shared with the inspection pause/step gate and TickClock) lets the host hold Tick N until all sources' inputs for N have arrived;
- checkpoint World hashes can be exchanged between peers to detect desync, and the hash-trail comparison from the inspection tooling localises where it began;
- join-in-progress is a save (to a stream or bytes) plus the input log from that Tick, using the existing save and replay machinery;
- the determinism contract already covers Windows/Linux x86-64 for the same build and Plugin set; ARM64/macOS cross-play is best effort and the replay header and handshake should carry the build and Plugin set identity so peers can verify they match.
Not supported or promised: rollback netcode (needs fast in-memory snapshots, the later snapshot-ring option), and state-replication models (client-server snapshots of Entities); the per-chunk change feed might help a Game author build the latter, but it is not a designed goal.

**Handed forward:** package layout places the input envelope, replay log, verifier and a wire-format helper; the scheduler needs an Input Phase and the shared Tick gate; the inspection tooling's replay-check consumes this log; the testing strategy gains replay-equivalence tests (record, replay, compare hashes) and a simulated two-peer lockstep test in the determinism harness.

**Amendment (CPU-only v1):** the 'without Approximate GPU Field reads' qualifier in Q9 has no v1 subject; bit-exact replay is guaranteed for fully Deterministic Plugin sets. Streaming focus points are ordinary Input Commands.
<!-- SECTION:FINAL_SUMMARY:END -->
