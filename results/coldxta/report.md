# LaunchLab report

Generated 2026-09-06 15:14 from `runs.csv` (20 runs, 2 configs).

## Measurement environment

- **generated_at**: 2026-09-06T14:44:11
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
- **runs_per_config**: 10
- **settle_seconds**: 2
- **interleaved**: True
- **configs**: arm64-coldxta <- C:\7z-arm64\7z.exe [coldxta, ARM64]; x64-coldxta <- C:\7z-x64\7z.exe [coldxta, x64]

> **CPU sampling was unavailable on this machine**, so `cpu_ms` and the CPU-by-module
> attribution are absent rather than zero. Everything else - scheduling, disk, paging -
> was recorded normally.

## Startup time per config

`startup_ms` = process create -> process exit, taken from the kernel process events in the trace.

| config | n | median ms | MAD ms | p95 ms | min ms | max ms | CV % | flaky runs |
|---|---:|---:|---:|---:|---:|---:|---:|---|
| arm64-coldxta | 10 | 18.9 | 0.3 | 36.9 | 18.5 | 36.9 | 27.4 | run 1 |
| x64-coldxta | 10 | 33.4 | 1.4 | 66.7 | 31.2 | 66.7 | 28.8 | run 1 |

Cross-check against the harness stopwatch (an independent clock; a large gap means the analyzer is measuring the wrong thing):

| config | median startup_ms (trace) | median wall_ms (stopwatch) |
|---|---:|---:|
| arm64-coldxta | 18.9 | 20.0 |
| x64-coldxta | 33.4 | 35.3 |

## Where the time goes

Medians per config. **These components overlap** (disk service time is served while the thread waits), so they rank contributors - they are not an additive budget of `startup_ms`. `ready_ms` and `wait_ms` are summed over every thread in the process, so on a multi-threaded workload they can legitimately exceed the wall-clock `startup_ms`.

`cpu_ms` reads 0.0 below only because this machine could not record CPU samples. `cpu_on_ms` - time actually running on a processor, from the context switch data - is exact either way, it just cannot be broken down by module without sampling.

| config | cpu_on_ms | cpu_ms | ready_ms | wait_ms | disk_service_ms | hardfault_io_ms | disk_ios | hardfaults | images_loaded |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| arm64-coldxta | 9.4 | 0.0 | 0.7 | 20.1 | 0.0 | 0.0 | 0.0 | 0.0 | 17.0 |
| x64-coldxta | 24.0 | 0.0 | 1.4 | 36.6 | 0.0 | 0.0 | 0.0 | 0.0 | 18.0 |

## Comparison against `arm64-coldxta`

| config | Δ median ms | ratio | Hodges-Lehmann shift ms | biggest component mover |
|---|---:|---:|---:|---|
| x64-coldxta | 14.6 | 1.8x | 14.1 | `wait_ms` +16.5 ms |

## Flaky runs

A run is flagged when it sits further than 3 MAD from its config median **and** more than 10% off it. The second bar matters: on a tight distribution 3 MAD can be a fraction of a millisecond, and a run nobody would call flaky would be flagged. The attribution is that run's component deltas against the same config's medians.

- **arm64-coldxta run 1**: 36.9 ms, +18.0 ms vs median. Largest mover `wait_ms` +10.1 ms (~56.3% of the delta).
- **x64-coldxta run 1**: 66.7 ms, +33.2 ms vs median. Largest mover `cpu_on_ms` +16.1 ms (~48.4% of the delta).

## Attribution

Medians across the runs of each config. A key missing from a run counts as zero for that run, so a cost that only appears occasionally does not masquerade as a typical one.

### Wait time by readying process

**arm64-coldxta** - top 10:

| key | median wait ms |
|---|---:|
| `7z.exe` | 10.1 |
| `conhost.exe` | 8.3 |
| `MsMpEng.exe` | 1.4 |
| `powershell.exe` | 0.3 |
| `csrss.exe` | 0.0 |

**x64-coldxta** - top 10:

| key | median wait ms |
|---|---:|
| `7z.exe` | 24.3 |
| `conhost.exe` | 8.6 |
| `XtaCache.exe` | 1.6 |
| `MsMpEng.exe` | 1.5 |
| `powershell.exe` | 0.7 |
| `csrss.exe` | 0.0 |

