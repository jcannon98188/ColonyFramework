---
id: TASK-1.19
title: Decide debug and inspection tooling
status: Done
assignee:
  - '@jason'
created_date: '2026-10-30 23:39'
updated_date: '2026-10-30 23:46'
labels:
  - 'wayfinder:grilling'
dependencies: []
parent_task_id: TASK-1
ordinal: 20000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Question: How can developers and tools inspect and debug a running or saved World? Cover: in-process inspection API vs out-of-process attach (shared memory or socket, read-mostly protocol; the Plugin decision deferred this here), schema/Entity/Component/Field browsing, live queries and read-only chunk views with structural-version safety, safe mutation (debug commands through the command buffer), time control (pause/step/Tick-scrub), replay and save inspection, hash-divergence finder for desync/determinism bugs (which chunk/Component/System diverged first), World diff between saves or ticks, visualisation hooks for Fields and spatial structures given the Framework is headless, and whether a reference inspector app is in scope or only the protocol. Inputs: Plugin/C ABI decision (inspection must not require Plugins), determinism contract, save/load container, scheduler.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Decided with the user (all twelve proposals accepted). Builds on the profiling/diagnostics records and hash trail, the Plugin C ABI decision (inspection must not require Plugins), the determinism contract, the save container and structural versions.

**Shape (Q1):** one versioned inspection protocol (request/response messages over a byte-stream transport). An in-process server speaks it so a Game author can embed a debug console; out-of-process tools attach over a local socket or named pipe. No shared memory in v1 (a copy per request is fast enough and far simpler to keep safe).

**Hosting and security (Q2):** the host starts the server explicitly; off by default; compiled out entirely under a COLONY_NO_INSPECT configuration. Localhost-only bind, session token supplied by the host, read-only by default; writes require an explicit AllowMutation option. No sandbox or further authentication is claimed.

**Reads (Q3):** read-only view of schema (Components, Resources, Relations, Fields, Systems, Plugins with layout hashes, ordering, access), Entities (filter by archetype/Component set, decoded values, Relations), Fields (region, z-slice, point, per-chunk metadata and versions), Resources and event queues, spatial index queries, and live diagnostics records. Requests are serviced at Tick boundaries, so no tearing and no locking against Systems.

**Mutation (Q4):** with AllowMutation, edits are debug commands through the normal command-buffer path in a dedicated debug lane applied at the next Phase boundary. Each is recorded in the replay log with its Tick so debugged runs stay replayable; the manifest flags hand-edited Worlds. No direct memory writes are ever exposed.

**Time control (Q5):** pause (a gate the host loop honours), step N Ticks, change Tick rate where the host uses TickClock. No built-in rewind: load a save and replay inputs to a target Tick using the replay verifier. An in-memory snapshot ring is a possible later option, not v1.

**Desync debugging (Q6):** protocol tools over the hash trail: trail compare reports the first diverging Tick, Phase and System; narrowing drills per-archetype/per-chunk hashes, then per-column hashes, then a value diff of differing rows (Entity ID, Component, field, both values); replay check re-runs a save plus input log with the trail enabled. The test harness uses the same workflow to explain failures.

**Diff (Q7):** World/save diff between two Worlds, two saves, or a live World and a save, at Entity, Component and Field-chunk level with summary and drill-down; Entities match by stable ID; per-section checksums let unchanged sections be skipped without decoding.

**Visualisation (Q8):** data, not pixels: slice/region reads with a suggested colour mapping (named palette or range) plus Entity position lists for overlays. No renderer shipped.

**Reference tooling (Q9):** the protocol plus a command-line reference client (colony-inspect: list, get, query, diff, trail-compare, replay-check), which doubles as the protocol conformance test and scripting target. A graphical inspector is out of scope (community or a separate web project).

**Plugins (Q10):** Plugins (C# and C ABI) can register custom inspectors (named views over managed state or derived data, sharing the managed-state callback hook) and custom debug commands. Plugin Components are inspectable automatically through the schema.

**Format (Q11):** compact binary protocol with a JSON mapping for scripting and the CLI; request/response plus a diagnostics subscription stream; versioned separately from the C# API and C ABI; handshake reports version and features; schema travels with the data so older clients degrade gracefully.

**Cost and safety (Q12):** server runs on its own thread, queues requests and services them between Ticks within a time budget (long queries paginated or cancelled); allocates only outside the Tick path using the scratch arena or its own buffers; zero cost with no client connected.

**Handed forward:** package layout must place the protocol, server, CLI client and the COLONY_NO_INSPECT configuration; the scheduler needs a pause/step gate and a debug command lane; the replay log must record debug commands; the save container's per-section checksums are relied on by diff.
<!-- SECTION:FINAL_SUMMARY:END -->
