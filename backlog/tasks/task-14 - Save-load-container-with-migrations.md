---
id: TASK-14
title: Save/load container with migrations
status: Done
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-10-31 13:49'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-9
  - TASK-12
documentation:
  - >-
    backlog/docs/spec/doc-1 -
    Colony-Framework-architecture-spec-v1-part-1-of-2.md
  - >-
    backlog/docs/spec/doc-2 -
    Colony-Framework-architecture-spec-v1-part-2-of-2.md
type: task
ordinal: 41000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec section 7.1: a save is a snapshot of canonical chunk memory (little-endian, CPU-independent) in one sectioned container with header, manifest (schema, Plugin set, Tick, World hash, seed), table of contents and one section per Field chunk and per archetype, with per-section xxHash, LZ4-class compression, atomic write with backup generations. Rebuildable state is not saved. A schema table embedded in the save lets matching layout hashes bulk-copy while mismatches run registered chained per-Component migrations; no migration means a clear failure. The acceptance that matters most is from section 6: load-then-continue is bit-identical to never saving.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 world.Save writes the sectioned container atomically; World.Load builds a fresh World from it; a scenario saved at Tick T and continued to T+K hashes identically to an unsaved run at T+K
- [x] #2 Per-section hashes are verified on load and corruption of any section fails with a clear error naming the section
- [x] #3 A Component whose layout hash changed is migrated through registered N to N+1 migrations chained across versions; a missing migration fails loading with a clear message
- [x] #4 Golden saves per container version are committed and loaded in tests; compression is per section and optional
- [x] #5 Background save via copy-on-write snapshot produces a save identical to a synchronous save at the same Tick
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Merged into integration/v1 at d97c30f (2026-10-31). Full solution Release green twice (680 total, 1 skipped AOT); Colony.Persistence.Tests 38. New package src/Colony.Persistence (Core only). Container v1: header/manifest/TOC, one section per archetype and per Field chunk, per-section XXH64, in-house deterministic LZ4 block codec (optional per section; small sections stay raw), atomic write (temp+flush+rename) with .bak1/.bak2 generations. Load-then-continue identity verified for colonist, fields and claims scenarios on 1 and 4 workers (T=6, K=8, hash checked every Tick); background save (copy-on-write snapshot while 10 Ticks run on 2 workers) byte-identical to synchronous save. Corruption names the section (entities, archetype/N, table of contents); truncation/foreign bytes -> SaveFormatException. Migrations: chained per-Component N->N+1 keyed by layout hash; missing link names the Component and both layout hashes; same layout bulk-copies and verifies the hash. Goldens: tests/Colony.Persistence.Tests/Golden/v1/*.clnysave (2-4 KB). Core: additive internal seams only (World.Persistence.cs, EntityIndex.Restore, Archetype.AppendChunk/RecountEnabled, BlobStore.RestoreBlob) + public IWorldExtension.Save/Load default hooks with ExtensionStateWriter/Reader; World hash layout unchanged. Fields: FieldSet implements Save/Load; SpatialIndex.Load => Rebuild(). Scenario fixes for bit-identical continuation: ColonistScenario keeps recovered total in ColonyStats; ClaimScenario rebuilds its site list in Begin; Scenario.OnLoaded(World) hook in Colony.Testing. Also closes the TASK-22 save/restore clause (claims round trip). Follow-ups: Load takes a WorldSchema and the manifest Plugin set is an empty placeholder until TASK-16; Resource and extension layout changes are not migratable (spec 7.1 mandates Component migrations only); change versions/LastRun are rebuilt conservatively on load (every chunk marked changed once) so a ChangeFilter System sees all chunks on the first Tick after load - saving per-System LastRun would be needed for bit-identical continuation of such scenarios; ClaimScenario not yet in the AOT host.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Colony.Persistence: sectioned save container (manifest, TOC, per-section XXH64, optional deterministic LZ4, atomic write with backup generations), World.Load building a fresh World, chained per-Component migrations with clear failures, committed golden saves, and copy-on-write background saves identical to synchronous ones. Save-at-T / continue-to-T+K is hash-identical to never saving across scenarios and worker counts. Merged at d97c30f.
<!-- SECTION:FINAL_SUMMARY:END -->
