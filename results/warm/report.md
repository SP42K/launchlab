# LaunchLab report

Generated 2026-09-06 15:14 from `runs.csv` (40 runs, 2 configs).

## Measurement environment

- **generated_at**: 2026-09-06T14:41:12
- **machine**: WIN11
- **os**: Microsoft Windows 11 Pro build 26200.8037
- **architecture**: ARM64
- **cpu**: Apple silicon
- **logical_cpus**: 4
- **ram_gb**: 8
- **machine_model**: VMware, Inc. VMware20,1
- **virtual_machine**: True
- **hypervisor_present**: True
- **system_disk_media**: SSD
- **on_ac_power**: no battery (AC)
- **defender_realtime**: True
- **power_plan**: Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)
- **wpr_profile**: launchlab-nosampling.wprp
- **cpu_sampling**: False
- **app_args**: 
- **runs_per_config**: 20
- **settle_seconds**: 2
- **interleaved**: True
- **configs**: arm64-warm <- C:\7z-arm64\7z.exe [warm, ARM64]; x64-warm <- C:\7z-x64\7z.exe [warm, x64]

> **CPU sampling was unavailable on this machine**, so `cpu_ms` and the CPU-by-module
> attribution are absent rather than zero. Everything else - scheduling, disk, paging -
> was recorded normally.

## Startup time per config

`startup_ms` = process create -> process exit, taken from the kernel process events in the trace.

| config | n | median ms | MAD ms | p95 ms | min ms | max ms | CV % | flaky runs |
|---|---:|---:|---:|---:|---:|---:|---:|---|
| arm64-warm | 20 | 19.3 | 0.5 | 25.0 | 18.5 | 47.9 | 30.6 | run 1, run 3 |
| x64-warm | 20 | 33.8 | 0.3 | 43.7 | 32.9 | 65.9 | 20.5 | run 1, run 13 |

Cross-check against the harness stopwatch (an independent clock; a large gap means the analyzer is measuring the wrong thing):

| config | median startup_ms (trace) | median wall_ms (stopwatch) |
|---|---:|---:|
| arm64-warm | 19.3 | 20.6 |
| x64-warm | 33.8 | 35.6 |

## Where the time goes

Medians per config. **These components overlap** (disk service time is served while the thread waits), so they rank contributors - they are not an additive budget of `startup_ms`. `ready_ms` and `wait_ms` are summed over every thread in the process, so on a multi-threaded workload they can legitimately exceed the wall-clock `startup_ms`.

`cpu_ms` reads 0.0 below only because this machine could not record CPU samples. `cpu_on_ms` - time actually running on a processor, from the context switch data - is exact either way, it just cannot be broken down by module without sampling.

| config | cpu_on_ms | cpu_ms | ready_ms | wait_ms | disk_service_ms | hardfault_io_ms | disk_ios | hardfaults | images_loaded |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| arm64-warm | 9.5 | 0.0 | 0.8 | 20.4 | 0.0 | 0.0 | 0.0 | 0.0 | 17.0 |
| x64-warm | 23.0 | 0.0 | 1.3 | 33.0 | 0.0 | 0.0 | 0.0 | 0.0 | 18.0 |

## Comparison against `arm64-warm`

| config | Δ median ms | ratio | Hodges-Lehmann shift ms | biggest component mover |
|---|---:|---:|---:|---|
| x64-warm | 14.5 | 1.7x | 14.6 | `cpu_on_ms` +13.5 ms |

## Flaky runs

A run is flagged when it sits further than 3 MAD from its config median **and** more than 10% off it. The second bar matters: on a tight distribution 3 MAD can be a fraction of a millisecond, and a run nobody would call flaky would be flagged. The attribution is that run's component deltas against the same config's medians.

- **arm64-warm run 1**: 47.9 ms, +28.6 ms vs median. Largest mover `wait_ms` +18.3 ms (~64.2% of the delta).
- **arm64-warm run 3**: 25.0 ms, +5.7 ms vs median. Largest mover `wait_ms` +5.7 ms (~99.6% of the delta).
- **x64-warm run 1**: 65.9 ms, +32.1 ms vs median. Largest mover `wait_ms` +17.0 ms (~52.9% of the delta).
- **x64-warm run 13**: 43.7 ms, +9.9 ms vs median. Largest mover `wait_ms` +18.7 ms (~188.0% of the delta).

## What the tracing costs

The same scenario with the recorder off (`-NoTrace`), by harness stopwatch. Every provider left enabled is overhead charged to the thing being measured, so it is measured rather than assumed.

| config | median wall_ms traced | median wall_ms untraced | overhead ms | overhead % |
|---|---:|---:|---:|---:|
| arm64-warm | 20.6 | 54.5 | -33.9 | -62.3 |
| x64-warm | 35.6 | 76.0 | -40.3 | -53.1 |

## Attribution

Medians across the runs of each config. A key missing from a run counts as zero for that run, so a cost that only appears occasionally does not masquerade as a typical one.

### Wait time by readying process

**arm64-warm** - top 10:

| key | median wait ms |
|---|---:|
| `7z.exe` | 10.0 |
| `conhost.exe` | 8.5 |
| `MsMpEng.exe` | 1.5 |
| `powershell.exe` | 0.3 |
| `csrss.exe` | 0.0 |

**x64-warm** - top 10:

| key | median wait ms |
|---|---:|
| `7z.exe` | 19.4 |
| `conhost.exe` | 8.4 |
| `XtaCache.exe` | 3.1 |
| `MsMpEng.exe` | 1.4 |
| `powershell.exe` | 0.7 |
| `csrss.exe` | 0.0 |

