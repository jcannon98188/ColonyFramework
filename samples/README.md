# samples/

Consumers of the Framework written the way a Game would write them (spec section 14). In `Colony.slnx`, built by
CI, never shipped, never referenced by `src/`. The C native host for the C ABI (spec section 8) lands with TASK-28.

## HelloColony: the minimal headless host

The hello-colony sketch of spec section 11.1 as a console host. `Sketch.cs` is the sketch as it compiles today
(generated Components and System, a `heat` Field, 100 colonists, 1000 Ticks, save, print the hash);
`HelloColony.Tests/SpecSketchDocTests` diffs it against the spec's code block and documents the exact
deviations (the spec follow-up). `HelloColonyHost.cs` wraps it with flags.

```
dotnet run -c Release --project samples/HelloColony                        # 100 colonists, 1000 Ticks, colony.save, prints hash 0x...
dotnet run -c Release --project samples/HelloColony -- --sketch            # the literal spec sketch
dotnet run -c Release --project samples/HelloColony -- --ticks 0 --no-save --counters --pace 16 --colonists 100000
dotnet run -c Release --project samples/HelloColony -- --trace trace.json --level Detailed
```

Options: `--ticks N` (1000; 0 = run until an inspection client sends `shutdown`, needs `--inspect`),
`--colonists N` (100), `--workers N` (1; 0 = physical cores), `--save path` (colony.save) or `--no-save`,
`--counters` (attach `MeterBridge`; prints the `dotnet-counters monitor` command), `--trace out.json` (Perfetto
Chrome Trace Event JSON of every Tick; open at https://ui.perfetto.dev), `--level Standard|Detailed`, `--pace ms`
(sleep between Ticks), `--replay out.replay` (record a replay log; enables `rewind`), `--sketch`.

Inspection (spec section 10.2): `--inspect PORT` (0 = any free port) starts the loopback `InspectionServer` and
prints `inspect: port N token T`; `--inspect-token T` fixes the token (random otherwise); `--allow-mutation`
registers the debug Input Command and lets `colony-inspect mutate` through (the schema and World hash then
differ from the plain sketch). The host pumps requests between Ticks and blocks on the server while paused.

```
dotnet run -c Release --project samples/HelloColony -- --ticks 0 --no-save --inspect 0 --allow-mutation --replay colony.replay
dotnet run --project tools/Colony.Inspect -- status --port <N> --token <T>      # then schema, entities, field heat 0 0, pause, step 5, rewind 100, live, shutdown
```

Built with `-p:ColonyNoInspect=true` (COLONY_NO_INSPECT) the server does not exist and `--inspect` exits 2.

Native AOT (the host is the AOT proof for generated code, Fields, Persistence and the Diagnostics sinks together):

```
dotnet publish samples/HelloColony -c Release -r win-x64      # or linux-x64
artifacts/publish/HelloColony/release_win-x64/HelloColony.exe --no-save
```

`HelloColonyHostTests.The_native_aot_build_of_the_host_prints_the_same_hash_as_the_jit_run` runs the published
executable and compares its hash with the in-process JIT run; it skips when nothing is published (CI publishes
it before `dotnet test`). This host replaces `tests/Colony.Diagnostics.CountersHost` (TASK-18's interim host).

## HelloLockstep: two or three peers over loopback sockets

Lockstep multiplayer on `Colony.Persistence`'s transport-free `LockstepSession` (TASK-36; guide: `docs/guides/lockstep-networking.md`). The sockets live here only (`TcpMesh.cs`); `PeerHost.cs` is the Game loop. Founders start together, a third peer joins mid-run from a `JoinPackage` sent over the same sockets.

```
dotnet run -c Release --project samples/HelloLockstep -- --seat 0 --ports 5000,5001,5002 --active 0,1
dotnet run -c Release --project samples/HelloLockstep -- --seat 1 --ports 5000,5001,5002 --active 0,1
dotnet run -c Release --project samples/HelloLockstep -- --seat 2 --ports 5000,5001,5002 --join-from 0     # a few seconds later
```

Options: `--ticks N` (300), `--tick-ms N` (2, sleep per Tick so founders do not outrun a joiner), `--join-margin N` (60 Ticks), `--workers N`. Each peer prints `hash 0x...`; they must be equal. `HelloLockstep.Tests` runs it in-process and as separate processes.
