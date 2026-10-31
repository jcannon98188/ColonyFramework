# Procedural world generation approaches (DF, RimWorld, Minecraft, Qud, Factorio, WFC, noise/graph generators)

Research for the planned world-generation building blocks. Facts gathered 2026-10-30. Terms follow GLOSSARY.md (Framework, Game author, World, Field, Plugin, Definition, Kernel, Backend).

## Confidence and method

Source access was limited: several sites rate-limited or blocked fetches, and no talk video or slide deck could be read. Labels used:

- **[P]** first-party source read directly (developer blog, official docs, official changelog, author's repo).
- **[W]** community wiki page that tracks the game's own data/code (Minecraft Wiki, mappings sites). Treat as good but secondary.
- **[S]** developer statement reported in an interview transcript or write-up (secondary, but the developer's own words).
- **[I]** my inference from the above, not a claim by the source.
- **[U]** unverified: widely believed or remembered but not confirmed from a source I could read.

Biggest gaps: Dwarf Fortress internals (no technical primary source reachable), RimWorld's vanilla GenStep order and RNG isolation (only the 1.6 changelog read), Factorio's chunk seeding detail, all GDC/IRDC talk contents (only titles and abstracts seen), Philox paper body (PDF unreadable by the fetch tool).

## 1. Per-game findings

### Minecraft (Java) - the best-documented staged, chunked pipeline

- **Staged chunk pipeline** [W]. Chunks advance through ordered statuses: `empty`, `structure_starts`, `structure_references`, `biomes`, `terrain`, `features`, `initialize_light`, `light`, `spawn`, `full`. Source: https://minecraft.wiki/w/Chunk_format and https://minecraft.wiki/w/World_generation. Note the wiki says `noise`, `surface` and `carvers` were merged into `terrain` in a 26.3 snapshot, so status names are version-unstable. Proto-chunks (all but `full`) can be left at an early stage and resumed when the player approaches.
- **Noise router** [W]. Terrain is a set of data-driven density functions (JSON), wired through a "noise router" with named roles: `final_density` (positive = solid), aquifer fields (`barrier`, `fluid_level_floodedness`, `fluid_level_spread`, `lava`), ore-vein fields (`vein_toggle`, `vein_ridged`, `vein_gap`), and biome-only climate fields (`temperature`, `vegetation`, `continents`, `erosion`, `depth`, `ridges`). Source: https://minecraft.wiki/w/Noise_router. This is the most relevant precedent: one pure function of position per concern, composed declaratively, replaceable by data. The router was added to noise settings in 1.18.2 pre-release and slide settings moved into density functions in 22w12a (same source via search result).
- **Structure placement** [W]. `random_spread` divides the world into a `spacing` x `spacing` chunk grid, tries one structure per cell, offsets within `0..spacing-separation-1`, with `linear` or `triangular` distribution, and a per-set `salt`. Source: https://minecraft.wiki/w/Structure_set. This is a region-keyed pure function: the position of any structure start can be computed from the seed and region coordinate without generating neighbours. Structure starts are stored per chunk and neighbouring chunks hold `structures.references` to the chunk that owns the start (Chunk_format page); pieces that cross a border are clipped, so each chunk places only its own part (World_generation, Features section).
- **Cross-chunk features** [W]. Per chunk, the game gathers biomes from the chunk and its 8 neighbours; features may write blocks outside the chunk but only within a nearby 3x3 area; 11 ordered decoration steps, structure pieces before features within each step. Source: https://minecraft.wiki/w/World_generation (Features section).
- **Seeding** [P-ish, via Fabric Yarn javadoc, a mapping of the game's code]. `ChunkRandom` derives a population seed from the world seed and the chunk's block corner, then a per-feature "decorator seed" from population seed, feature index and generation step (`index + 10000 * step` in the 2020 version). Only the low 48 bits of the world seed affected population/carver output in that era because of `setSeed` truncation. Source: https://maven.fabricmc.net/docs/yarn-1.17.1+build.38/net/minecraft/world/gen/ChunkRandom.html (and sibling versions). Overworld generation later moved to xoroshiro128++ (World_generation page links to it) [W].
- **Known non-determinism pitfall (the key lesson)** [W]. Features spill into already-generated neighbours, so "chunk generation order determines which of two conflicting features overwrites or suppresses the other", and two worlds with the same seed and version can differ depending on the player's route. Sources: https://minecraft.wiki/w/Feature and World_generation, which cites bug MC-55596. Each stage is a pure function of seed and coordinate, but the composition is order-dependent once stages write into neighbours.

### RimWorld

- **Step list with explicit order, data-driven** [S/P-mix]. Map generation is a list of GenSteps each with an `order` value, defined in XML Defs, so mods can add or reorder them (a Steam workshop thread shows a modder changing a settlement GenStepDef `order` to 400 in `BaseFactionMapGenerator.xml`, and notes XPath patching is the recommended method and that two mods editing the same step conflict). Source: https://steamcommunity.com/workshop/filedetails/discussion/1570960972/1744478429683206000 (community, not first-party). I could not read the vanilla order list [U].
- **1.6 changes (official changelog)** [P]. Ludeon's 1.6 public changelog (https://docs.google.com/document/d/e/2PACX-1vRCjqVtPQDFGu4POiKTUd_8o3U2Asdhx99SOvcgU66ABdYtk3Cgndd53yJ6BC4tZX530pp_m6lf4Z9P/pub) says rivers, mountains, caves and coasts are now "map features"; added noise visualisation tools for maps and the world; added a dev command to generate maps on a world tile and set a tile's biome; and fixed "Various sources of non-deterministic map/world generation." A search-result summary of a developer document additionally claims "mutators" that run at several points across generation, step order revisited, and dev tools "Regenerate Current Map Stepped" and "Do Next Gen Step" [U: I could not read that document; treat as a lead].
- **World layer** [W]. The planet is a tile grid with elevation, temperature, rainfall, hilliness driving biome choice; same seed gives the same land masses, rivers and mountains, but roads are generated independent of the world seed so they can differ between runs. Source: https://rimworldwiki.com/wiki/World_generation (community wiki). Roads being outside the seed is a documented example of a determinism hole.
- **Not found:** RNG isolation (the commonly cited `Rand.PushState` pattern) [U].

### Dwarf Fortress

- Primary technical sources were not reachable (wiki rate-limited; interview pages I read had no worldgen detail). What I can support: Adams describes his approach to world and story generation as identify the basic elements, find the governing rules, set them in motion (https://www.gamedeveloper.com/pc/feature-the-making-of-i-dwarf-fortress-i-, [S]); the team prioritises "narratively interesting" aspects, builds systems as semi-independent pieces that interconnect over time in a shared framework, and works on a 2006 codebase (https://www.gamedeveloper.com/design/-i-dwarf-fortress-i-figuring-out-how-to-simulate-the-universe-one-step-at-a-time, [S]).
- The widely known structure - a fixed-size world generated up front (elevation, temperature, rainfall, drainage, rivers, biomes), then a history simulation of civilisations over years, with a local map derived from the world map at embark - is [U] here. Verify against the DF wiki `World generation` page and Adams's GDC 2016 talk before relying on it. Notably this is a bounded-world, simulate-history-then-play design, which matches the repo's "bounded World in v1" decision [I].

### Caves of Qud

- **Layered, abstract-to-concrete construction** [S]. Bucklew (Software Engineering Daily ep. 1801, https://softwareengineeringdaily.com/wp-content/uploads/2025/01/SED1801-Caves-of-Qud.txt) describes villages built by starting from abstract context (location, founding date, ruling Sultanate) and adding detail in steps: divide area into screens, assign terrain with layered noise, place buildings, add paths/roads/rivers/bridges. Ruin interiors are often WFC; higher layout uses space partitioning. WFC "becomes repetitive across large, asymmetric areas" so it is combined with other methods. Designers author WFC inputs as small images.
- **History is generated, then rationalised** [S, same source]. History is closer to fiction than simulation: plausible motives are generated after the fact from context, and when no matching context exists a random pick becomes new context.
- **Static backbone plus generated fill** [S]. Towns mostly fixed, wilderness/dungeons/parts of lore generated (https://www.gamedeveloper.com/design/tapping-into-the-potential-of-procedural-generation-in-caves-of-qud).
- Talks to read (titles confirmed, contents not read): GDC 2019 "End-to-End Procedural Generation in Caves of Qud", GDC 2019 "Tile-Based Map Generation using Wave Function Collapse in 'Caves of Qud'", Roguelike Celebration 2019 "Dungeon Generation via Wave Function Collapse", IRDC 2015 "Data-Driven Engines of Qud and Sproggiwood". Index: https://wiki.cavesofqud.com/wiki/Caves_of_Qud/Videos.
- Seeding, on-demand zone generation and modding of generators: **not addressed** in the sources I could read [U].

### Factorio - the cleanest "pure function of position" design

- **Everything is a noise expression of x,y** [P]. FFF-390 (https://factorio.com/blog/post/fff-390): the generator knows only a tile's position and maps it to tile type and entities (trees, rocks, resources, decoratives, cliffs, enemies). Expressions are compiled into a linear "noise program" with dependencies resolved, built for computing all data at once, SIMD-optimised and multithreaded; three programs per surface (tiles, cliffs, entities+decoratives); constant folding and common-subexpression dedup; a reported chunk cost falling from 18.35 ms to 2.83 ms in one case and 4.8 ms to 3.58 ms average. Basis noise on a grid got a special fast path.
- **Autoplace** [P]. FFF-258 (https://factorio.com/blog/post/fff-258): every autoplaceable tile/entity/decorative gets a probability and richness per point; highest-probability tile wins; entities placed by probability. Resource patches use "spot noise": the map is split into regions; per region, random candidate points get density/quantity/radius/favorability, are sorted by favorability, and chosen until a target quantity is reached; richness is a cone peaking at the centre, perturbed by basis noise. A separate rule set gives the starting area guaranteed resources (iron, copper, coal, stone, no uranium/oil) kept away from water.
- **Seeds and determinism** [P]. The official docs (https://lua-api.factorio.com/latest/auxiliary/noise-expressions.html) define `seed0` (normally `map_seed`, 32-bit) and `seed1` as a named layer ID (string via CRC32 or small sequential number); for `spot_noise`, identical seed, region and spacing settings yield identical candidate points. Same-seed-same-output is implied rather than stated [I]. FFF-390 does not say how neighbouring chunks agree at borders; the position-only design implies it [I].
- **Extensibility** [P]. Mods define `probability_expression` / `richness_expression` on autoplace specs, named expressions/functions as prototypes, and override per property via `MapGenSettings::property_expression_names`; name resolution order is local scope, property overrides, global prototypes, built-ins. A debug aid: verbose logging prints an estimated complexity per noise program. Gotcha from the docs: `if` evaluates all branches; integer x,y with integer input scale makes `basis_noise` return 0.
- **Limitation visible in the design** [I]. Spot noise needs a region-level pass (sorting candidates by favorability across a region), so it is not a pure per-tile function; correct behaviour depends on regions being computed identically by any chunk that touches them.

### Wave Function Collapse

- Author's repo (https://github.com/mxgmn/WaveFunctionCollapse) [P]: overlapping model (NxN patterns from a sample, N typically 3) and simple tiled model (adjacency rules); repeat observe (lowest nonzero entropy cell, collapse by weights) and propagate (constraint propagation). A contradiction (a cell with zero possible states) ends the run with no output; the page calls contradictions "surprisingly rare", says deciding whether a bitmap has other valid outputs is NP-hard, and does not describe built-in restarts or backtracking. It supports user constraints (autocompleting hand-made levels), combines with other methods (e.g. as a local-defect fixer), mentions block-wise modification for lazily generating unbounded output and GPU ports that propagate in parallel, but explicitly does not present chunking as a core feature.
- [I] Implication: WFC is a sequential global-constraint solver, a poor fit for per-chunk pure functions or GPU kernels. It works as a bounded local generator (a room, a ruin, a prefab) whose input is seeded by region key and retried on contradiction with a derived sub-seed. Qud uses it exactly this way (ruin interiors).

### Noise-based and graph-based generators (general)

- Noise-based: Factorio and Minecraft both show terrain as a stack of position-pure noise fields (continentalness/erosion/ridges style in Minecraft; elevation/moisture/aux in Factorio), then thresholds, then discrete placement.
- Graph-based (rooms/dungeons, road networks, civilisation maps): no primary source read. [U] General pattern: a coarse graph is made first (seed-keyed), then geometry is carved to satisfy it, which is how connectivity is guaranteed by construction rather than checked after the fact [I].

## 2. Cross-cutting analysis

### Staged pipelines
All the staged systems share the same shape [I from above]: ordered passes, each owning one concern (shape, climate, hydrology, materials, resources, structures, decoration, history), each reading earlier results. They differ in whether passes are data-driven (Minecraft JSON density functions and features, RimWorld GenStepDef `order`, Factorio expressions) or code (DF, Qud's builders). The data-driven ones are the ones with a documented modding story.

### Determinism and seeding
- Keyed streams per (seed, purpose, coordinate): Minecraft (world seed + chunk corner + feature index + step), Factorio (seed0 + named seed1 layer), structure sets (salt). This is the counter-based RNG idea in practice [I]; the repo's Philox decision is consistent with it and removes the 48-bit truncation class of bug [I].
- Philox/counter-based RNG background: Salmon et al., "Parallel Random Numbers: As Easy as 1, 2, 3", SC11 (Philox, Threefry, ARS; designed for per-key independent streams and GPU use). I could only fetch the PDF as binary, so claims about BigCrush results and speed are **[U]**. Paper: https://www.thesalmons.org/john/random123/papers/random123sc11.pdf.
- Determinism failures seen in primary/near-primary sources: (a) order-dependent neighbour writes (Minecraft, MC-55596); (b) outputs not keyed to the seed (RimWorld roads, per the community wiki); (c) RimWorld's own changelog admits "various sources of non-deterministic map/world generation" were fixed in 1.6. These are the three failure types a determinism contract must test for.
- Float behaviour across platforms and thread scheduling are not discussed by any source I read [U]; the repo's integer/fixed-point GPU decision is not contradicted.

### Chunked/streaming generation and cross-chunk features
Three strategies appear [I]:
1. **Pure position function** (Factorio noise, Minecraft density functions): any chunk can be generated in isolation, borders match for free. Needs no neighbour access.
2. **Region-keyed placement** (Minecraft structure sets; Factorio spot noise): the existence and location of a large feature is computed from a coarse grid cell, so every chunk can independently know about it; each chunk then paints only its own part (Minecraft clips pieces to the chunk; `structures.references` points chunks at the owning start).
3. **Neighbour-writing decoration** (Minecraft features within a 3x3 window): easy to author, but introduces order dependence unless generation of a chunk is defined purely in terms of the *seed-derived content of its neighbourhood* rather than "whatever neighbours exist so far". Minecraft evidently accepts the order dependence.
Fix pattern [I]: make decoration a pure function of a halo (generate base data for the 3x3 neighbourhood, then derive the centre chunk's decoration from it, deterministically), or restrict cross-chunk features to ones owned by a region key with deterministic write rules and a fixed conflict priority.

### Performance and GPU suitability
- Factorio is the only source with numbers and a design for throughput: compile expression graphs to a linear program, batch, SIMD, thread per chunk, fold constants [P].
- Density/noise passes are embarrassingly parallel per cell and map naturally onto Kernels [I]. Sorting/selection (spot noise), flood fills/connectivity, WFC, and history simulation are not, and should stay on the CPU [I]. No source I read discusses GPU worldgen directly [U].

### Extensibility and modding
- Data-driven expression/functions with named overrides (Factorio: `property_expression_names`), JSON noise settings (Minecraft), ordered step Defs with an `order` number (RimWorld). Documented pain: mods editing the same step conflict; ordering by a single integer is brittle (RimWorld Steam thread; 1.6 revisiting order is also a sign of that, per the changelog summary [U]).
- Qud designers author WFC inputs as images (Bucklew) - content as data input to an algorithm.

### Validation and debugging tools
- RimWorld: noise visualisation for map and world, dev command to generate a map on a tile, set a tile's biome (1.6 changelog [P]); stepped regeneration tools [U].
- Factorio: per-program complexity estimate in verbose logs [P].
- Minecraft: no first-party tool found in my reads; a seed is the repro handle.
- [I] What the Framework should add for itself: per-stage hash of output per chunk (golden hashes), regenerate-one-chunk-in-isolation vs in-context comparison to catch order dependence, a border-seam checker, reachability/connectivity validators, and visualisation of each noise field.

### Known pitfalls
| Pitfall | Evidence |
|---|---|
| Order-dependent results from neighbour writes | Minecraft Feature/World_generation pages, MC-55596 [W] |
| Subsystem outside the seed | RimWorld roads [W]; 1.6 non-determinism fixes [P] |
| Seed truncation / weak seed mixing | Minecraft population/carver seed used only 48 bits [P-ish] |
| Structure clipped at chunk borders / duplicates | Minecraft clips pieces per chunk; references tracked per chunk [W] |
| Starting-area fairness needs special rules | Factorio separate start-area spot settings [P]; RimWorld start spot picks largest contiguous open area (1.6) [P] |
| Contradictions / no output | WFC [P]; restart policy is the implementer's job [I] |
| Unreachable areas, bad connectivity | Primary sources read did not discuss; Qud builds roads/paths after placement [S]. Handle by construction plus validators [I] |
| Version drift changes worlds | Minecraft status names change by version [W]; RimWorld updates may change seeds (community disagrees) [U] |

## 3. Implications for the Framework (all inferences, [I])

Building blocks the Framework should supply:
- **Keyed RNG streams**: an API like `Rng.For(worldSeed, streamId, coord...)` over the already-decided Philox, with stream ids as strings hashed (Factorio's `seed1` precedent) so Plugins cannot collide by accident.
- **Generation as ordered stages** with declared reads/writes of Fields, and per-stage dependency on a halo radius (0 for pure noise; 1 chunk for decoration), so a ChunkProvider can schedule and cache correctly.
- **Position-pure sampling library**: gradient/value noise, fractal combinators, Voronoi/spot placement, region-keyed candidate points, all with CPU reference plus Exact (integer/fixed-point) Kernel versions.
- **Region-keyed placement helper** (spacing/separation/salt style) and chunk-ownership rules for structures that span chunks (owner chunk computes the start; others clip).
- **Compile/fuse step** for declarative noise graphs (Factorio's linear program with constant folding) as an optional optimisation.
- **Generator validation and debugging**: golden-hash tests per stage, isolation-vs-context check, seam checker, field visualisers, per-stage cost report, stage-stepping.
- **Plugin extension points**: register a stage with ordering constraints (before/after by name, not a bare integer), override named expressions, supply Definitions for biomes/ores/structures.

Game author territory:
- The actual world content: biome set, ore tables, structure templates, climate model, history/faction simulation and its narrative layer, prefab and WFC tilesets, balance and fairness rules (starting area guarantees), and the choice between bounded history-first (DF-like) and streamed terrain. The Framework may supply generic WFC/graph helpers but the inputs are content.

Open questions worth a follow-up: DF's actual stage list and rejection criteria; RimWorld vanilla GenStep order and RNG isolation; whether to define cross-chunk decoration as halo-pure (determinism-safe, costs extra compute) or Minecraft-style (cheaper, order-dependent) - the determinism contract points to halo-pure; connectivity guarantees for caves/rivers across chunk borders (no source read addresses this directly).

## Sources index
- Minecraft Wiki: Chunk format, World generation, Structure set, Noise router, Feature (https://minecraft.wiki/w/...) [W]
- Fabric Yarn ChunkRandom javadoc: https://maven.fabricmc.net/docs/yarn-1.17.1+build.38/net/minecraft/world/gen/ChunkRandom.html
- Factorio FFF-258, FFF-390, Lua API noise expressions (URLs above) [P]
- RimWorld 1.6 changelog (URL above) [P]; RimWorld Wiki World generation [W]; Steam discussion on GenStepDef order [community]
- WFC: https://github.com/mxgmn/WaveFunctionCollapse [P]
- Caves of Qud: SED ep. 1801 transcript; Game Developer article; Qud wiki video list
- Dwarf Fortress: two Game Developer interviews (limited content)
- Salmon et al. SC11 (Random123), not readable
