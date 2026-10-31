---
id: TASK-18
title: 'Diagnostics sinks: EventSource/Meter bridge and Perfetto trace export'
status: Done
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-10-31 12:50'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-17
documentation:
  - >-
    backlog/docs/spec/doc-1 -
    Colony-Framework-architecture-spec-v1-part-1-of-2.md
  - >-
    backlog/docs/spec/doc-2 -
    Colony-Framework-architecture-spec-v1-part-2-of-2.md
type: task
ordinal: 45000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec section 10.1: the diagnostics records are only useful once something can read them from outside the process. Two opt-in sinks ship in Colony.Diagnostics on top of the read API: an EventSource/Meter bridge for dotnet-counters and OpenTelemetry, and a Chrome-trace/Perfetto JSON export for timeline inspection. Sinks are host-side and must never affect simulation state or Tick allocation.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 With the Meter bridge enabled, dotnet-counters shows per-Tick and per-System counters from a running sample host
- [x] #2 A Perfetto trace exported from a captured run opens in the Perfetto UI showing Phases, Systems and worker lanes
- [x] #3 Enabling a sink changes no World hash and introduces no steady-state allocation on Framework threads
- [x] #4 Sinks consume the versioned record layout and fail clearly on a version mismatch
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Merged into integration/v1 at 7b20dd0 (2026-10-31). Full solution Release green (576 total, 1 skipped AOT). Sinks are host-driven (Capture()/Publish() between Ticks, not IDiagnosticsListener) so no sink code runs inside World.Tick; both copy into ctor-allocated arrays (zero alloc verified on host and worker threads with both sinks attached, World hash identical with and without sinks). MeterBridge: Meter 'Colony' (configurable) with tagged per-System/worker/counter gauges, plus process-wide 'Colony-Diagnostics' EventSource (TickCompleted event, EventCounters); verified in-process with MeterListener/EventListener and for real with dotnet-counters against tests/Colony.Diagnostics.CountersHost (105k Entities, 4 workers: colony.tick.duration 0.83 ms, colony.system.duration[system=Wander] 0.06 ms, colony.ticks 32/s). PerfettoTrace: Chrome Trace Event JSON; Ticks laid back to back; Detailed level = job slices on worker lanes and exact System spans, Standard = Systems placed sequentially in schedule order (args.approximate=true) plus one busy slice per worker per Tick. Orchestrator opened the sample trace (40 Ticks, Detailed, 2.3 MB) in ui.perfetto.dev v59 via postMessage: it loads as 'colony-trace (3 MB)', 248.9 ms, process 'Colony World 1' with nested tracks; lane names (Ticks/Phases/Systems/Worker N) are asserted by PerfettoTraceTests on the parsed JSON. Cosmetic: an 'Unnamed SliceTrack' appears at the top level (probably the Tick instant markers lacking a pid/tid) - fix-pass item. Version check: RecordLayoutMismatchException names sink, expected and actual; checked in every sink ctor and per captured record. Test gotchas: an enabled EventListener makes the runtime allocate on the publishing thread, so EventSource and zero-alloc test classes share a non-parallel xUnit collection; Diagnostics.Tests runs with tiered compilation off. Follow-ups: attach MeterBridge/TraceCapture to the TASK-30 sample host and retire CountersHost; inspection-protocol sink is TASK-29 scope; optional listener-mode TraceCapture.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Opt-in host-side diagnostics sinks in Colony.Diagnostics: a Meter/EventSource bridge visible in dotnet-counters (per-Tick and per-System counters) and a Chrome-trace/Perfetto JSON export with Tick, Phase, System and worker lanes that loads in ui.perfetto.dev. Neither changes the World hash nor allocates on Framework threads; both verify the record layout version and fail clearly on mismatch. Merged at 7b20dd0.
<!-- SECTION:FINAL_SUMMARY:END -->
