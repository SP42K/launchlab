# LaunchLab report

Generated 2026-09-06 15:14 from `runs.csv` (12 runs, 1 configs).

## Measurement environment

- **generated_at**: 2026-09-06T15:01:38
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
- **app_args**: spike --spin-ms 120 --write C:\lab\spike.bin --mb 32
- **runs_per_config**: 12
- **settle_seconds**: 1
- **interleaved**: True
- **configs**: spike <- C:\lab\launchlab\src\LaunchLab\bin\Release\net8.0\launchlab.exe [warm, ARM64]

> **CPU sampling was unavailable on this machine**, so `cpu_ms` and the CPU-by-module
> attribution are absent rather than zero. Everything else - scheduling, disk, paging -
> was recorded normally.

## Startup time per config

`startup_ms` = process create -> process exit, taken from the kernel process events in the trace.

| config | n | median ms | MAD ms | p95 ms | min ms | max ms | CV % | flaky runs |
|---|---:|---:|---:|---:|---:|---:|---:|---|
| spike | 12 | 192.0 | 3.2 | 679.0 | 187.8 | 679.0 | 60.1 | run 1 |

Cross-check against the harness stopwatch (an independent clock; a large gap means the analyzer is measuring the wrong thing):

| config | median startup_ms (trace) | median wall_ms (stopwatch) |
|---|---:|---:|
| spike | 192.0 | 194.7 |

## Where the time goes

Medians per config. **These components overlap** (disk service time is served while the thread waits), so they rank contributors - they are not an additive budget of `startup_ms`. `ready_ms` and `wait_ms` are summed over every thread in the process, so on a multi-threaded workload they can legitimately exceed the wall-clock `startup_ms`.

`cpu_ms` reads 0.0 below only because this machine could not record CPU samples. `cpu_on_ms` - time actually running on a processor, from the context switch data - is exact either way, it just cannot be broken down by module without sampling.

| config | cpu_on_ms | cpu_ms | ready_ms | wait_ms | disk_service_ms | hardfault_io_ms | disk_ios | hardfaults | images_loaded |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| spike | 175.1 | 0.0 | 1.8 | 1192.5 | 10.6 | 0.0 | 43.0 | 0.0 | 30.0 |

## Flaky runs

A run is flagged when it sits further than 3 MAD from its config median **and** more than 10% off it. The second bar matters: on a tight distribution 3 MAD can be a fraction of a millisecond, and a run nobody would call flaky would be flagged. The attribution is that run's component deltas against the same config's medians.

- **spike run 1**: 679.0 ms, +487.0 ms vs median. Largest mover `wait_ms` +4086.4 ms (~839.1% of the delta).

## Attribution

Medians across the runs of each config. A key missing from a run counts as zero for that run, so a cost that only appears occasionally does not masquerade as a typical one.

### Disk read service time by file

**spike** - top 10:

| key | median service ms |
|---|---:|
| `C:\Users\user\AppData\Local\Temp\launchlab-5ab80320\fixed-spike\launchlab.dll` | 0.0 |

### Disk write service time by file

**spike** - top 10:

| key | median service ms |
|---|---:|
| `C:\lab\spike.bin` | 10.3 |
| `C:\$LogFile` | 0.2 |
| `C:\Users\user\AppData\Local\Temp\launchlab-5ab80320\fixed-spike\launchlab.dll` | 0.0 |

### Wait time by readying process

**spike** - top 10:

| key | median wait ms |
|---|---:|
| `launchlab.exe` | 1074.2 |
| `Idle` | 119.9 |
| `conhost.exe` | 9.0 |
| `MsMpEng.exe` | 1.5 |
| `powershell.exe` | 0.4 |
| `csrss.exe` | 0.0 |
| `System` | 0.0 |

