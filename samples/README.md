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

Options: `--ticks N` (1000), `--colonists N` (100), `--workers N` (1; 0 = physical cores), `--save path`
(colony.save) or `--no-save`, `--counters` (attach `MeterBridge`; prints the `dotnet-counters monitor`
command), `--trace out.json` (Perfetto Chrome Trace Event JSON of every Tick; open at https://ui.perfetto.dev),
`--level Standard|Detailed`, `--pace ms` (sleep between Ticks), `--sketch`.

Native AOT (the host is the AOT proof for generated code, Fields, Persistence and the Diagnostics sinks together):

```
dotnet publish samples/HelloColony -c Release -r win-x64      # or linux-x64
artifacts/publish/HelloColony/release_win-x64/HelloColony.exe --no-save
```

`HelloColonyHostTests.The_native_aot_build_of_the_host_prints_the_same_hash_as_the_jit_run` runs the published
executable and compares its hash with the in-process JIT run; it skips when nothing is published (CI publishes
it before `dotnet test`). This host replaces `tests/Colony.Diagnostics.CountersHost` (TASK-18's interim host).
