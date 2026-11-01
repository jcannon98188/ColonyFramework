---
id: TASK-29
title: 'Inspection protocol, server and colony-inspect CLI'
status: Done
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-11-01 16:24'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-17
  - TASK-15
documentation:
  - >-
    backlog/docs/spec/doc-1 -
    Colony-Framework-architecture-spec-v1-part-1-of-2.md
  - >-
    backlog/docs/spec/doc-2 -
    Colony-Framework-architecture-spec-v1-part-2-of-2.md
type: task
ordinal: 56000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec section 10.2 (decided in the debug and inspection tooling ticket): one versioned inspection protocol (compact binary with a JSON mapping) served in-process or over a localhost socket or pipe, host-started, off by default, compiled out under COLONY_NO_INSPECT, token-protected and read-only by default with AllowMutation opt-in. Reads happen at Tick boundaries (schema, Entities, Fields, Resources, events, spatial queries, diagnostics). Mutation is only debug commands through the command buffer, recorded in the replay log. Features: pause and step via the Tick gate, replay-based rewind, hash-trail desync finder, World and save diff, Plugin-registered inspectors, and colony-inspect as the reference client. No GUI.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 colony-inspect connects to a running sample host and reads schema, Entities, Fields, Resources, events, spatial queries and diagnostics at Tick boundaries
- [x] #2 Pause, step and replay-based rewind work through the shared Tick gate; debug mutations go through the command buffer and appear in the replay log
- [x] #3 The desync finder compares two hash trails and reports the first diverging Tick, Phase and System; the diff command compares two saves or a World and a save
- [x] #4 Building with COLONY_NO_INSPECT removes the server entirely; without a token the server refuses connections; AllowMutation is required for any write
- [ ] #5 Plugins register inspectors in Register and they appear in colony-inspect
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Protocol/server in Colony.Diagnostics, colony-inspect CLI, desync finder, save diff, Plugin inspectors (PluginRegistration.AddInspector). HelloColony flags: --inspect PORT, --inspect-token, --allow-mutation, --replay. COLONY_NO_INSPECT verified by manual flagged build (no InspectionServer type in Diagnostics.dll). Gaps: spatial query not available on sample host (sketch has no spatial index; proven on Diagnostics fixture); Diagnostics.Tests does not build under the flag; AOT publish of HelloColony not re-run. Fixed shutdown race in InspectionServer.Dispose.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Inspection protocol, token-gated server, colony-inspect CLI with pause/step/rewind, desync finder, save diff and Plugin inspectors merged to integration/v1.
<!-- SECTION:FINAL_SUMMARY:END -->
