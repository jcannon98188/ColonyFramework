---
id: TASK-6
title: 'Entity storage tracer bullet: slabs, chunks, archetypes, command buffer'
status: To Do
assignee: []
created_date: '2026-10-31 01:56'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-3
documentation:
  - >-
    backlog/docs/spec/doc-1 -
    Colony-Framework-architecture-spec-v1-part-1-of-2.md
  - >-
    backlog/docs/spec/doc-2 -
    Colony-Framework-architecture-spec-v1-part-2-of-2.md
type: task
ordinal: 33000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The heart of Colony.Core (spec section 4.1 and 4.2, ADR 0001): archetype/chunk structure-of-arrays storage with 64 KiB chunks, 64-byte aligned columns padded to a multiple of 16 elements independent of CPU vector width, carved from roughly 4 MiB native slabs. Entity IDs are 64-bit (32-bit index, 32-bit generation) and stable across save/load. Structural change happens only through command buffers at sync points, which is what makes parallel iteration safe later. The ECS prototype (research report prototype-ecs-bench) validated this shape at 100k and 1M colonists with zero allocation; this ticket builds the real thing with a model-based test as the correctness oracle. Component identity is a registered name plus layout hash, never the CLR type.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Entities with unmanaged Components can be created, destroyed and have Components added or removed through a command buffer played back at a sync point; stale EntityIds fail the generation check
- [ ] #2 Chunk-level iteration over an archetype exposes each column as a Span with 64-byte alignment and 16-element padding regardless of host CPU
- [ ] #3 A model-based test runs random operation sequences against a dictionary-based reference ECS and compares observable state after every playback
- [ ] #4 A 100k-Entity create, iterate and mutate loop performs zero managed allocations in steady state, verified by a test
- [ ] #5 Opt-in non-fragmenting mode exists for Components flagged as flipping often, and a per-Tick scratch bump arena backs command buffers and temporaries
- [ ] #6 Debug builds validate refs and Spans against the chunk structural version, poison freed memory and report leaks on World dispose
<!-- AC:END -->
