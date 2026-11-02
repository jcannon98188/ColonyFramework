# Colony Framework

A headless .NET framework that game authors build colony-simulation games on (in the vein of Dwarf Fortress and RimWorld). It supplies the hard simulation machinery so authors don't have to.

## Language

**Framework**:
Colony Framework itself: the reusable simulation machinery, excluding any game built on it.
_Avoid_: Engine, library

**Game author**:
A developer who builds a game on top of the Framework.
_Avoid_: User, consumer, modder

**Modder**:
A person who extends a finished game using extension points a Game author exposes.
_Avoid_: Game author, user

**World**:
The complete simulated state of one game: its Entities, Fields and time.
_Avoid_: Map, level, scene

**Entity**:
An individual thing in the World that has identity, such as a colonist, an item or a creature.
_Avoid_: Object, actor, GameObject

**Component**:
A unit of data attached to an Entity that describes one aspect of it.
_Avoid_: Property, attribute

**Resource**:
World-level data that belongs to no Entity, such as the current time or the random seed.
_Avoid_: Global, singleton, settings

**Relation**:
A named link from one Entity to another that can be queried from either end, such as a hierarchy or "stored in".
_Avoid_: Reference, pointer, association

**System**:
Logic that runs over Entities with particular Components each Tick.
_Avoid_: Manager, behaviour

**Field**:
A grid of per-tile values spanning the World, such as terrain, temperature or fluid level. Fields hold tile data; tiles are not Entities.
_Avoid_: Layer, map, tilemap

**Tick**:
One fixed step of simulated time; the only unit in which the World advances.
_Avoid_: Frame, update

**Plugin**:
A unit of code or data that registers Components, Systems or content with the Framework through its extension points.
_Avoid_: Mod, addon, module

**Definition**:
A data-driven description of a kind of content, such as an item type or creature species, that a Game author or Modder adds without writing code.
_Avoid_: Template, prototype, config

**Backend**:
An interchangeable implementation of GPU compute behind the Framework's GPU abstraction.
_Avoid_: Driver, renderer

**Migration**:
A piece of code that converts saved Component or Resource data from an older layout, or an extension's saved sections from an older format version, to the current one when a save is loaded.
_Avoid_: Upgrade, patch, converter

**Kernel**:
A unit of GPU compute work that updates Fields, paired with a CPU reference implementation of the same behaviour.
_Avoid_: Shader, job

**Phase**:
A named stage of a Tick (such as Input, Simulate or End) in which Systems run in a fixed order.
_Avoid_: Stage, step, pass

**Input Command**:
A typed, recorded request from a player, script or tool that changes the World at a stated Tick.
_Avoid_: Event, message, action

**Claim**:
A Relation by which an Entity reserves a target (such as a work item) so no other Entity can take it.
_Avoid_: Lock, reservation

**Movement class**:
A Game-defined kind of mover (such as walker or flyer) with its own rules for which tiles it can cross.
_Avoid_: Layer, agent type

**Residency**:
Whether a chunk of the World is Active (simulated), Hibernating (in memory, not simulated) or Unloaded (stored only).
_Avoid_: Loading state, LOD

**Pinned Entity**:
An Entity that stays loaded and simulated regardless of which chunks are resident.
_Avoid_: Persistent, global entity
