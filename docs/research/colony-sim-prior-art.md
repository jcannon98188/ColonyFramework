# Colony-sim prior art: hard problems and known techniques

Research for TASK-1.4. Question: how do Dwarf Fortress, RimWorld and similar games solve pathfinding at scale, job assignment, fluid/temperature simulation, off-screen simulation, and save/load of huge worlds? What pitfalls and techniques should shape Framework scope?

Researched 2026-10-30. Terms follow `GLOSSARY.md` (World, Entity, Field, Tick, System).

## Source quality and gaps

Each claim cites its source. Labels: **[P]** primary (developer's own words, talk, blog or dev forum post), **[W]** community wiki (not developer-maintained, version-dependent), **[S]** secondary.

Gaps. The DF wiki rate-limited the fetcher (HTTP 429) for most of the session, so its pages for Path, Pressure, Job and Temperature were seen only as search-result summaries, not read in full. No primary source was found for:

- DF or RimWorld job assignment internals (RimWorld's job-giver design appears only in a modder tutorial).
- DF temperature internals.
- Off-screen simulation in either game.
- Save/load architecture of either game.

These areas are marked **UNVERIFIED** or **GAP** below. Follow-up options: the DF Bay 12 devlogs, the Dwarf Fortress Steam-era GDC talks, and the RimWorld decompiled source (modding docs).

## 1. Pathfinding at scale

### Core finding: A* is not the hard part, unreachable targets and a changing map are

**Dwarf Fortress**
- Uses A*. Tarn Adams says jump point search does not fit "since our map changes so much". [P: Stack Overflow Blog interview, https://stackoverflow.blog/2021/12/31/700000-lines-of-code-20-years-and-one-developer-how-dwarf-fortress-is-built/]
- Keeps connected-component IDs of walkable regions. Before running A*, an agent compares its component ID with the target's. Unreachable targets are skipped, so A* does not flood the whole map. [P: same Stack Overflow Blog interview; also P: 2008 Game Developer interview, https://gamedeveloper.com/design/interview-the-making-of-dwarf-fortress]
- Cost: component IDs must be recomputed (flood fill) when terrain connectivity changes, for example when water splits an area or digging joins one. Adams called this connectivity bookkeeping a major cost of the "slow and creaky" pathing. Alternatives such as rectangle-based pathing were judged too memory-hungry. [P: Game Developer 2008 interview]
- Flyers do not get the walking component data, so their pathing is cruder and relies on short-range flood fills. A separate index for fliers would be "a large memory and speed hit". Adams mentions stairs tracking and limited path caching only as possible future gains. [P: Stack Overflow Blog]
- Map scale: up to 480x480xZ, or 768x768xZ at maximum settings. [P: Game Developer 2008 interview]
- Wiki: A* with an admissible heuristic. In edge cases it can search every tile on the map, which is why enclosing a fortress helps. Old versions note that floods and anti-floods recheck connectivity map-wide whenever liquids move, causing lag until they settle. [W: https://dwarffortresswiki.org/Path, seen only via search summary]

**RimWorld**
- Tynan Sylvester, "Reachability at last" (2013). A* with an unreachable goal scans the whole map before giving up, producing a roughly 10x frame hitch. A weapon in a sealed room during a raid could make colonists retry the path every frame and freeze the game. [P: https://ludeon.com/blog/2013/07/reachability-at-last]
- Fix: flood-fill the map into regions (a zone index shared by connected squares) and skip pathing when indices differ. Regenerating everything cost about 5.5 ms on a 200x200 map, too slow per frame, so changes (walls built or destroyed) regenerate local reachability only. [P: same blog post]
- Later forum remark by Tynan: regions are used to speed up distant targets such as stone for cutting, and beyond about 40 cells of path distance the old global search is used. [S: relayed via search summary of a Ludeon forum thread, https://ludeon.com/forums/index.php?msg=34212; **verify** against the thread]

**Factorio** (a similar scale problem; FFF-317, https://www.factorio.com/blog/post/fff-317)
- A hierarchical abstract map: 32x32 chunks, each split into connected components of walkable tiles, storing only the edge tiles. [P]
- Two cooperating searchers. A "Reverse Resumable A*" runs backward from the goal over the abstract graph and supplies the heuristic. The normal A* finds the real path. [P]
- The abstract map ignores entities (cliffs, trees, buildings), so it only needs updating on terrain changes. The abstract result is a heuristic and not a guarantee, which the authors note as a risk for unreachable targets. [P]
- The old straight-line heuristic was poor for long detours around lakes. Responses could take "many ticks". [P]

### Techniques to adopt or consider
1. Cheap reachability pre-check (connected-component or region ID) before any search. Both DF and RimWorld arrived at it independently after hitting the same failure.
2. Incremental, local component maintenance on map edits, not full rebuild. The cost of keeping it current is the real design problem.
3. Optional hierarchical abstraction (chunks, then components) as a heuristic provider. It tolerates dynamic entities by ignoring them.
4. Time-sliced or asynchronous path requests, so a long search spans multiple Ticks (Factorio's model).
5. Separate movement classes (walkers, fliers, swimmers). Each needs its own connectivity data. DF's cost for fliers shows this should be a Framework-level concept (movement mode and its connectivity index) and not left to each Game author.

### Pitfalls
- Unreachable-target retry loops (RimWorld freeze). The job layer must back off or invalidate tasks, not retry every Tick.
- Connectivity invalidated by fluids: water flooding changes walkability constantly in DF.
- Z-levels multiply the cost. Stairs, ramps and vertical connectivity must be in the connectivity model.
- Regions that ignore dynamic obstacles (doors, other agents) give a heuristic and not a guarantee.

## 2. Job and task assignment

Evidence here is thinner (see Gaps).

**Dwarf Fortress**
- A labor is a permission, not a job. A job is the concrete task a dwarf performs. Skill affects quality and speed, not eligibility. [W: https://dwarffortresswiki.org/index.php/v0.34:Labor, via search summary]
- Hauling queues scale with the number of dwarves holding the hauling labor, so many haulers create many simultaneous haul tasks. Trade-goods hauling has top priority and can delay workshop jobs. Changing a labor does not interrupt a job in progress. [W: DF wiki Haul page, via search summary]
- A dwarf with no job in a busy fort is often stuck, for example behind a floodgate, so unreachable or stuck states need to be visible to the player. [W: DF wiki Job page, via search summary]

**RimWorld**
- Behaviour is a data-driven think tree (XML) of nodes: priority selectors, conditional nodes and sub-trees. A JobGiver node yields a Job, the Job is executed as a sequence of Toils, and fallback nodes such as random wandering catch the case where no job is produced. [S: modder tutorial on Ludeon forums, https://ludeon.com/forums/index.php?msg=177011 (Alpha 12d, now old); the fetch of this page was blocked (HTTP 403), so this is search-snippet level]
- Player-facing work priorities have two levels, work type and work giver (finer grain). **UNVERIFIED**: found only through community material and a mod changelog. No Tynan or Ludeon explanation was found.

**Takeaways (design inference, not sourced)**
- Pull model: agents query eligible work (by permission, priority, reachability, distance). This pairs with the reachability checks above.
- A data-driven priority tree, replaceable by Plugins, matches the Framework's Plugin/System model.
- Must cover: reservation or claiming (avoid two agents taking one job), job invalidation, pre-checking reachability before assignment, and per-agent permission and priority tables.

## 3. Fluid and temperature simulation

### Fluids (Dwarf Fortress)
- 3D cellular automaton. Each cell holds 0-7 units of water, i.e. "large tiles rather than many small particles". Water moves down first, otherwise sideways. [P: Game Developer 2008 interview]
- Pressure is nonlocal. When water cannot move locally, it flood-fills downward through full cells to look for an outlet. If one is found, water "teleports" there. If not, the cell is flagged static and skipped until conditions change. The static flag is credited with keeping fluid cost low. [P: same interview]
- Pitfalls reported by the author:
  - Local rules made water pile into pyramids at waterfalls.
  - Update order matters (bottom-up versus top-down changes how fast a column spreads).
  - A known counter bug caused a three-quarter split.
  - The outlet search may rise only to about one level below the origin height, so U-tubes work but water cannot exceed its start height.
  [P: same interview]
- Wiki: the game tracks fluid tiles deeper than 1/7; pressure can push water up stairs and ramps; diagonal gaps block pressure. [W: https://dwarffortresswiki.org/Pressure and 40d:Water_flow, via search summary]
- Coupling: fluid changes alter connectivity, which forces the pathfinding recomputation in section 1.

### Temperature
- **DF**: per-item and per-tile heat exchange each tick: an object moves toward the environment's temperature by the difference divided by its specific heat. Fire spreads to adjacent flammable tiles. [W: DF wiki Temperature/Heat, via search summary; **UNVERIFIED in full**]. The DF framerate guide lists weather and temperature as costs players disable. [W: 23a:Maximizing_framerate, via search summary only]
- **RimWorld**: temperature is per room, not per tile. All tiles in an enclosed room share one temperature. Equalization with neighbours and the outdoors runs once every 120 ticks. Walls conduct only in cardinal directions. Unroofed rooms snap to outdoor temperature. Heaters and coolers add or remove heat on each equalization tick. [W: https://rimworldwiki.com/wiki/Temperature, flagged by its editors for rewrite and fact-check]
  - A third-party decompile analysis also cites a 120-tick room recalculation (low confidence). [S: https://devizomb.github.io/Rimworld-Internals//001-room-temperature-equalization-en.html]
  - Forum reports exist of odd behaviour when merging rooms. [S: https://ludeon.com/forums/index.php?msg=442227]
- **Oxygen Not Included**: described by community sources as a per-cell simulation with neighbour exchange. No Klei primary source was found. **GAP**.

### Takeaways
- Two contrasting models. Per-cell Field (DF, ONI) is general and emergent but costs per tile. Per-region aggregate (RimWorld rooms) is cheap and produces readable gameplay but needs a room detection step and has merge and split edge cases.
- Techniques that appear repeatedly:
  - Sleep or static flags for cells in equilibrium.
  - Low-frequency updates (every N ticks) for slow-moving quantities.
  - Update-order effects: results can depend on iteration order, so decide on double-buffering or deterministic ordering.
  - Nonlocal rules (pressure) are hard in a local cellular automaton and were solved with special-case searches.
- This maps directly to the Framework's Field concept. A fixed-step Field update with active-region tracking is the shared mechanism, and the update rule should be the Game author's choice. The "GPU Backend" glossary entry fits per-cell stencil updates well, but nonlocal rules like DF pressure do not map easily to GPU.

## 4. Simulating off-screen areas

Primary sources on DF and RimWorld are largely absent (**GAP**). What was found:

- DF is not documented here as time-slicing or reducing fidelity for unobserved areas of one map. Tarn Adams says the game is "at the edge of what we can currently support" and that, apart from graphics, the simulation is single-threaded. [P: Stack Overflow Blog]
- DF separates world generation (a coarse historical simulation of figures, sites and civilizations) from local play. Biomes come from layered fields (elevation, temperature, rainfall, drainage). [P: Game Developer 2008 interview, which covers world-gen inputs only]. How the world simulation continues while a fortress is played was not covered by any source retrieved. **GAP**.
- Factorio's approach is the best documented active-set pattern found:
  - Entities and chunks are only updated while active. Idle roboports dropped from about 1 ms to 0.025 ms per tick on a test save.
  - Chunks needing updates are kept in buckets with reference counters, and one bucket is processed per tick.
  - Robots store a movement intention and are rendered predictively, updating every 20 ticks (moving) or 60 ticks (stationary), with gains of about 10-25% on robot-heavy saves.
  - Multithreading helped independent work (a synthetic 77k-combinator save went from 131 s to 8.2 s) but not memory-bound work (electric networks: CPU use rose from 0.5% to 15% with no overall gain).
  [P: https://www.factorio.com/blog/post/fff-421; also forum discussion on active entity lists in FFF-204, S]
- RimWorld runs the active map fully. Off-map factions and caravans are abstract world-map objects. **UNVERIFIED**; no source retrieved.

### Takeaways
- The Framework needs an explicit concept of level of detail in time: active (full per-Tick), dormant (skipped or coarse-stepped), and catch-up (apply elapsed time analytically when a region wakes).
- Active-set scheduling (sleeping entities, bucketed updates, intention plus prediction) is proven and generalizes to Systems.
- Memory-bound systems do not gain from threads. Profile before parallelizing.

## 5. Save/load of huge worlds

Primary evidence is minimal (**GAP**).

- DF: the world save format was fixed early, and Adams has had to handle endianness differences across operating systems. He credits keeping "byte sizes under control" for an easy 32-to-64-bit port, and avoiding complicated pointer operations. He regards the deep polymorphic item hierarchy as a mistake in favour of a component-built item. [P: Stack Overflow Blog]
- Implication: persisted formats outlive code decisions. Versioning, explicit field widths and endianness, and avoiding pointer-shaped persistence reduce later cost.
- RimWorld's persistence (the Scribe system) and Factorio's save and map-generation design were not retrieved from primary sources. **GAP**.

Follow-up: Factorio blog posts on map saving and chunked generation, RimWorld's Scribe in decompiled or modding docs, and Minecraft's region-file format (chunked incremental persistence) would answer this section.

## 6. Cross-cutting lessons for Framework scope

1. A reachability or connectivity service is a first-class Framework facility, not a per-game feature. Both DF and RimWorld fixed the same freeze with it, and it is coupled to Fields (fluids and terrain edits change connectivity). It must support incremental update and multiple movement classes.
2. A job and reservation layer should sit on top of that reachability service (avoid retry storms). Its design needs a primary source before decisions (see gaps).
3. Fields must support active or static regions and a per-Field update rate. Pure per-Tick whole-grid updates do not scale (DF static flags, RimWorld 120-tick equalization).
4. Level of detail in time (dormant and catch-up) and active-set scheduling should be an early architectural decision, since retrofitting is hard (DF author notes the game is built on its original 2006 structure).
5. Persistence format versioning and a no-raw-pointer persistent model from the start.
6. Determinism and update order affect emergent behaviour (DF fluids). The Framework should fix and document iteration order.
7. Mixed fidelity models (per-cell Field versus per-room aggregate) are a Game-author decision. The Framework should support both rather than choosing one.

## 7. Candidate fog tickets (open questions this research did not close)
- How should the Framework represent time-level-of-detail and catch-up for dormant regions? (Need primary sources from DF devlogs or the Factorio design.)
- Job and reservation model: read RimWorld's decompiled source or modding docs for primary detail.
- Persistence: survey Minecraft, Factorio and RimWorld save architectures.
- Which Field update rules can run on GPU Backends (local stencils) versus which need CPU nonlocal passes (pressure)?
- Connectivity maintenance cost model: measure incremental versus full rebuild at the Framework's target World sizes.
- Re-read the DF wiki Path, Job, Temperature and Pressure pages in full when the rate limit clears, and check the unverified Tynan forum remark about the roughly 40-cell cutoff.
