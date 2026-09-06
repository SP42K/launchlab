# LaunchLab report

Generated 2026-09-06 15:10 from `runs.csv` (24 runs, 2 configs).

## Measurement environment

- **generated_at**: 2026-09-06T15:06:41
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
- **app_args**: h C:\lab\spike.bin
- **runs_per_config**: 12
- **settle_seconds**: 2
- **interleaved**: True
- **alternate_tracing**: False
- **configs**: arm64-warm <- C:\7z-arm64\7z.exe [warm, ARM64]; x64-warm <- C:\7z-x64\7z.exe [warm, x64]

> **CPU sampling was unavailable on this machine**, so `cpu_ms` and the CPU-by-module
> attribution are absent rather than zero. Everything else - scheduling, disk, paging -
> was recorded normally.

## Startup time per config

`startup_ms` = process create -> process exit, taken from the kernel process events in the trace.

| config | n | median ms | MAD ms | p95 ms | min ms | max ms | CV % | flaky runs |
|---|---:|---:|---:|---:|---:|---:|---:|---|
| arm64-warm | 12 | 29.8 | 0.2 | 395.1 | 29.2 | 395.1 | 173.6 | run 1, run 9 |
| x64-warm | 12 | 58.9 | 0.8 | 452.9 | 57.7 | 452.9 | 119.7 | run 1, run 8 |

Cross-check against the harness stopwatch (an independent clock; a large gap means the analyzer is measuring the wrong thing):

| config | median startup_ms (trace) | median wall_ms (stopwatch) |
|---|---:|---:|
| arm64-warm | 29.8 | 31.3 |
| x64-warm | 58.9 | 61.0 |

## Where the time goes

Medians per config. **These components overlap** (disk service time is served while the thread waits), so they rank contributors - they are not an additive budget of `startup_ms`. `ready_ms` and `wait_ms` are summed over every thread in the process, so on a multi-threaded workload they can legitimately exceed the wall-clock `startup_ms`.

`cpu_ms` reads 0.0 below only because this machine could not record CPU samples. `cpu_on_ms` - time actually running on a processor, from the context switch data - is exact either way, it just cannot be broken down by module without sampling.

| config | cpu_on_ms | cpu_ms | ready_ms | wait_ms | disk_service_ms | hardfault_io_ms | disk_ios | hardfaults | images_loaded |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| arm64-warm | 20.1 | 0.0 | 1.0 | 52.2 | 0.0 | 0.0 | 0.0 | 0.0 | 19.0 |
| x64-warm | 47.1 | 0.0 | 1.4 | 131.3 | 0.0 | 0.0 | 0.0 | 0.0 | 20.0 |

## Comparison against `arm64-warm`

| config | Δ median ms | ratio | Hodges-Lehmann shift ms | biggest component mover |
|---|---:|---:|---:|---|
| x64-warm | 29.1 | 2.0x | 28.7 | `wait_ms` +79.1 ms |

## Flaky runs

A run is flagged when it sits further than 3 MAD from its config median **and** more than 10% off it. The second bar matters: on a tight distribution 3 MAD can be a fraction of a millisecond, and a run nobody would call flaky would be flagged. The attribution is that run's component deltas against the same config's medians.

- **arm64-warm run 1**: 395.1 ms, +365.3 ms vs median. Largest mover `wait_ms` +980.4 ms (~268.4% of the delta).
- **arm64-warm run 9**: 34.5 ms, +4.7 ms vs median. Largest mover `wait_ms` +4.8 ms (~100.3% of the delta).
- **x64-warm run 1**: 452.9 ms, +394.0 ms vs median. Largest mover `wait_ms` +1772.4 ms (~449.9% of the delta).
- **x64-warm run 8**: 96.4 ms, +37.5 ms vs median. Largest mover `wait_ms` +145.4 ms (~387.3% of the delta).

## Attribution

Medians across the runs of each config. A key missing from a run counts as zero for that run, so a cost that only appears occasionally does not masquerade as a typical one.

### Disk read service time by file

**arm64-warm** - top 10:

| key | median service ms |
|---|---:|
| `C:\Users\user\AppData\Local\Temp\launchlab-5ffc62e4\fixed-arm64\7z.dll` | 0.0 |

**x64-warm** - top 10:

| key | median service ms |
|---|---:|
| `C:\Users\user\AppData\Local\Temp\launchlab-5ffc62e4\fixed-x64\7z.dll` | 0.0 |

### Wait time by readying process

**arm64-warm** - top 10:

| key | median wait ms |
|---|---:|
| `7z.exe` | 41.9 |
| `conhost.exe` | 8.2 |
| `MsMpEng.exe` | 1.5 |
| `powershell.exe` | 0.4 |
| `lsass.exe` | 0.2 |
| `csrss.exe` | 0.0 |
| `System` | 0.0 |
| `Idle` | 0.0 |
| `WmiPrvSE.exe` | 0.0 |

**x64-warm** - top 10:

| key | median wait ms |
|---|---:|
| `7z.exe` | 116.5 |
| `conhost.exe` | 8.3 |
| `XtaCache.exe` | 3.9 |
| `MsMpEng.exe` | 1.4 |
| `powershell.exe` | 0.8 |
| `lsass.exe` | 0.3 |
| `csrss.exe` | 0.0 |
| `System` | 0.0 |
| `Idle` | 0.0 |

