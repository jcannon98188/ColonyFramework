---
id: TASK-1.11
title: Decide save/load and versioning
status: Done
assignee:
  - '@jason'
created_date: '2026-10-30 13:46'
updated_date: '2026-10-31 01:38'
labels:
  - 'wayfinder:grilling'
dependencies:
  - TASK-1.5
  - TASK-1.6
parent_task_id: TASK-1
ordinal: 12000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Question: How is the World serialized (chunk snapshots of unmanaged memory, schema hash per Component, migrations between Framework and Plugin versions, incremental/background saves, compression) and what compatibility guarantees do saves have?
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Decided with the user (all twelve proposals accepted).

**Content (Q1):** a save is a snapshot of canonical chunk memory: archetype chunks and Field chunks written as they sit in memory (fixed, aligned, CPU-independent layout). Little-endian only. Rebuildable state is not saved (spatial index, Relation index, change versions, caches, GPU mirrors). Entities keep their stable 64-bit IDs.

**Container (Q2):** single file: header, manifest (schema, Plugin set, Tick, World hash, seed) and a table of contents addressing independent sections (one per Field chunk and per archetype). Region-file-like so partial reads and streaming loads are possible later; the same container serves full saves and per-chunk persistence for streaming worlds.

**Schema and migration (Q3):** the save embeds a schema table (Component name, layout hash, fields with types/offsets/widths). Matching hashes bulk-copy; mismatches run registered per-Component migrations N to N+1 (Game- or Plugin-supplied, chained; added fields default, removed fields dropped). No migration means the load fails with a clear report.

**Compatibility (Q4):** container format is versioned; the Framework reads all older container versions from 1.0 and refuses newer ones. Framework-owned Components have Framework-supplied migrations from 1.0; Game and Plugin Components are the Game author's responsibility via migration hooks. No promise during 0.x.

**Plugin set (Q5):** manifest lists Plugins and versions. Missing Plugin fails by default, with an opt-in "drop unknown Components" mode that discards and logs. Version differences go through migrations. Deterministic/NonDeterministic declaration is recorded.

**Integrity (Q6):** per-section xxHash checksums; manifest stores the World hash, recomputed and compared on load by default (opt-out), mismatch reports differing sections. Test mode: save, load, run N Ticks, compare against a never-saved run.

**Compression (Q7):** per-section, algorithm recorded per section; default LZ4-class, optional Zstd; uniform Field chunks stored as a single value; decompression uses the scratch arena (no managed allocation).

**Save timing (Q8):** saves only at a Tick boundary. Synchronous (blocking, default for small Worlds) or background via copy-on-write snapshot (chunks written by later Ticks copied lazily while a background thread serializes the frozen view; overhead bounded by chunks written during the save). Prototype validates the background mode cost.

**Incremental saves (Q9):** delta containers listing changed/deleted sections against a base, using per-chunk change versions; chained and periodically compacted. Optional in v1; container and TOC designed so it adds without a format break.

**Managed state and Resources (Q10):** Resources must be unmanaged serializable structs. Plugins holding managed state register serialize/restore callbacks (a named byte-blob section) or declare it derivable and rebuilt at load. Anything outside the World or a registered section is lost by definition; this is part of the Plugin contract.

**Crash safety (Q11):** write temp file, fsync, atomic rename; keep a configurable number of backup generations; never trust a file whose manifest checksum fails.

**API (Q12):** host-side only: world.Save(destination, options) and World.Load(source, options, plugins), Stream and file overloads, progress/cancellation hook. Load builds a fresh World. Autosave policy is the Game author's.

**Handed forward:** Plugin ticket must define migration hooks, managed-state callbacks, Plugin identity/version in the manifest and drop-unknown mode. Streaming-worlds fog reuses the per-section container. GPU Field prototype need not cover saves (GPU mirrors are rebuilt on load). ECS prototype should measure background-save copy-on-write cost. Testing strategy must include save/load round-trip determinism and migration tests.

**Amendment (CPU-only v1):** the GPU-mirror remark no longer applies (no GPU state exists to rebuild). Unloaded chunks use the same per-chunk sections: see Decide streaming worlds.
<!-- SECTION:FINAL_SUMMARY:END -->
