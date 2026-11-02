---
status: accepted
---
# Core shares internals with Persistence and Plugins; Plugins depends on Abstractions, Fields and Persistence

Spec section 3 said `InternalsVisibleTo` is for tests only. Two Framework packages need more of Core than its public API offers, and `Colony.Core.csproj` has granted them internals since TASK-14 and TASK-28 without a recorded decision (code review finding 8). We keep both grants and record what they cover and how they are kept small.

## What each package uses

- **`Colony.Persistence`** (TASK-14): it captures and restores the World exactly. It reads and writes chunk memory and archetype tables, the Entity index (generations, freed epochs, free list), Resource and event-queue bytes, the string table and blob store, and the per-World state restore entry points (`World.RestoreEntityIndex` and its siblings in `World.Persistence.cs`).
- **`Colony.Plugins`** (TASK-28, the C ABI host): a C Plugin has no CLR types, so it registers Components, Resources and Input Commands by name, size and alignment (`WorldBuilder.AddComponentLayout` / `AddResourceLayout`), declares Component data ranges for padding (`WorldBuilder.DeclareComponentData`, review finding 3), reads chunk columns, Resources and Inputs as raw bytes, records raw commands (`CommandBuffer.AddRaw`), and releases its handles when a World is disposed (`World.OnDispose`, review finding 7).

## Considered options

- **Keep the two grants and document them (chosen).** Both packages are lockstep-versioned with Core (spec section 3), so an internal seam cannot drift between releases. Persistence must reach memory the public API deliberately hides (raw chunk bytes, the free list), and the C ABI needs untyped registration that would be unsafe as public API.
- **Make the seams public (for example a `Colony.Unsafe`-style `[Experimental]` namespace).** Games would then see raw chunk restore and layout-only registration, the opposite of the "safe by default" public API. Rejected.
- **Move save/load and the C ABI host into Core.** Core would grow the container format, migrations and the C ABI, and every Game would carry them. Rejected: the spec splits them out on purpose.

## Constraints

- Only these two Framework packages get internals; every other grant is to a test project. `tools/Colony.DependencyCheck` already fixes which packages may reference Core, and a new non-test `InternalsVisibleTo` needs its own ADR.
- Internals used across the seam stay `internal`, carry a doc comment that names their caller, and are additive: Core keeps working, and its own tests stay green, without either package.
- Nothing in Persistence or Plugins may use the seam to change simulation results. Persistence restores state bit-exactly (load-then-continue is bit-identical, spec section 6). Plugins only registers and moves raw bytes through the same command and Resource paths C# Systems use.

## Colony.Plugins depends on more than Core

Spec section 3 listed `Plugins` as depending on `Core`. Since TASK-27/28 it references `Abstractions`, `Core`, `Fields` and `Persistence` (code review finding 9), and `tools/Colony.DependencyCheck` encodes exactly that set:

- **`Abstractions`**: the contracts a Plugin compiles against (`PluginManifest`, `IDefinitionSource`, `ColonyApi`); that is the package's purpose.
- **`Fields`**: Plugins register Fields through `PluginRegistration.Fields` (the Game's `FieldsBuilder`), and the generated `colony.h` covers Field registration and access.
- **`Persistence`**: the C ABI's `world_load` / `world_save` entries load and save through `WorldPersistence`.

All three edges point down the existing graph (none of these packages references `Plugins`; `Plugins` stays on the "never referenced" list), so there is no cycle, and a Game that does not use Plugins still never loads them. Splitting the C ABI into its own package was rejected for now: `Colony.Native` already is the separate shipping unit, and the managed host would only move, not shrink.

## Process-wide state outside the World

Spec section 3 says there is no static state or singletons. Three exceptions exist (code review finding 10); each holds no simulation state, never feeds a hash, and never decides an outcome, so many Worlds still coexist in a process:

- **The C ABI handle table** (`NativeHandles`, `Colony.Plugins`). C code holds plain integers that any entry may receive, so the table cannot hang off one World. Handles a World owns are released when it is disposed (review finding 7).
- **The C ABI last-error message** (`[ThreadStatic] ColonyAbi._lastError`). `colony_last_error` follows the C `errno` convention: the message of the last failed call on the calling thread. Per-thread, so concurrent Worlds on different threads cannot see each other's errors.
- **The `Colony-Diagnostics` EventSource** (`ColonyEventSource.Log`, `Colony.Diagnostics`). An `EventSource` name is registered once per process by the runtime, so the source is a singleton by design; its counters follow the first attached `MeterBridge`. It only observes.

Read-only caches of immutable data (for example the Testing harness's `DeterminismOptions` cache) are not state and need no entry here. A new process-wide field needs to be added to this list.

## Consequences

- Spec section 3 now names these two grants next to "tests only", and its dependency rules list the `Plugins` edges.
- CONVENTIONS.md (the implementer notes) says the same.
- Spec section 3 names the three process-wide exceptions next to "no static state or singletons".
