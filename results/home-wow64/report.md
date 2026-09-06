# LaunchLab report

Generated 2026-09-06 16:53 from `runs.csv` (40 runs, 2 configs).

## Measurement environment

- **generated_at**: 2026-09-06T16:14:12
- **machine**: DESKTOP-9C3SP52
- **os**: Microsoft Windows 11 Pro build 26200.9278
- **architecture**: AMD64
- **cpu**: AMD Ryzen 5 3600 6-Core Processor
- **logical_cpus**: 12
- **ram_gb**: 31.9
- **machine_model**: System manufacturer System Product Name
- **virtual_machine**: False
- **hypervisor_present**: True
- **system_disk_media**: SSD
- **on_ac_power**: no battery (AC)
- **defender_realtime**: True
- **power_plan**: Power Scheme GUID: 9935e61f-1661-40c5-ae2f-8495027d5d5d  (AMD Ryzen? High Performance)
- **wpr_profile**: launchlab.wprp
- **cpu_sampling**: True
- **app_args**: 
- **runs_per_config**: 20
- **settle_seconds**: 2
- **interleaved**: True
- **alternate_tracing**: False
- **configs**: x64-warm <- C:\7z-x64\7z.exe [warm, x64]; x86-warm <- C:\7z-x86\7z.exe [warm, x86]

## Startup time per config

`startup_ms` = process create -> process exit, taken from the kernel process events in the trace.

| config | n | median ms | MAD ms | p95 ms | min ms | max ms | CV % | flaky runs |
|---|---:|---:|---:|---:|---:|---:|---:|---|
| x64-warm | 20 | 18.2 | 0.6 | 19.7 | 16.3 | 23.5 | 8.7 | run 1 |
| x86-warm | 20 | 20.6 | 0.5 | 22.2 | 18.8 | 27.2 | 8.7 | run 1 |

Cross-check against the harness stopwatch (an independent clock; a large gap means the analyzer is measuring the wrong thing):

| config | median startup_ms (trace) | median wall_ms (stopwatch) |
|---|---:|---:|
| x64-warm | 18.2 | 19.7 |
| x86-warm | 20.6 | 22.3 |

## Where the time goes

Medians per config. **These components overlap** (disk service time is served while the thread waits), so they rank contributors - they are not an additive budget of `startup_ms`. `ready_ms` and `wait_ms` are summed over every thread in the process, so on a multi-threaded workload they can legitimately exceed the wall-clock `startup_ms`.

| config | cpu_on_ms | cpu_ms | ready_ms | wait_ms | disk_service_ms | hardfault_io_ms | disk_ios | hardfaults | images_loaded |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| x64-warm | 8.8 | 7.9 | 0.3 | 18.3 | 0.0 | 0.0 | 0.0 | 0.0 | 16.0 |
| x86-warm | 11.6 | 11.0 | 0.3 | 20.7 | 0.0 | 0.0 | 0.0 | 0.0 | 22.0 |

## Comparison against `x64-warm`

| config | Δ median ms | ratio | Hodges-Lehmann shift ms | biggest component mover |
|---|---:|---:|---:|---|
| x86-warm | 2.5 | 1.1x | 2.5 | `cpu_ms` +3.1 ms |

## Flaky runs

A run is flagged when it sits further than 3 MAD from its config median **and** more than 10% off it. The second bar matters: on a tight distribution 3 MAD can be a fraction of a millisecond, and a run nobody would call flaky would be flagged. The attribution is that run's component deltas against the same config's medians.

- **x64-warm run 1**: 23.5 ms, +5.4 ms vs median. Largest mover `wait_ms` +2.4 ms (~44.1% of the delta).
- **x86-warm run 1**: 27.2 ms, +6.6 ms vs median. Largest mover `wait_ms` +3.3 ms (~49.5% of the delta).

## Attribution

Medians across the runs of each config. A key missing from a run counts as zero for that run, so a cost that only appears occasionally does not masquerade as a typical one.

### CPU by module

**x64-warm** - top 10:

| key | median cpu ms |
|---|---:|
| `ntoskrnl.exe` | 4.6 |
| `ntdll.dll` | 1.5 |
| `amdkmdag.sys` | 0.0 |
| `Ntfs.sys` | 0.0 |
| `KernelBase.dll` | 0.0 |
| `luafv.sys` | 0.0 |
| `vgk.sys` | 0.0 |
| `FLTMGR.SYS` | 0.0 |
| `win32kbase.sys` | 0.0 |
| `WdFilter.sys` | 0.0 |

**x86-warm** - top 10:

| key | median cpu ms |
|---|---:|
| `ntoskrnl.exe` | 6.5 |
| `ntdll.dll` | 3.0 |
| `wow64.dll` | 0.0 |
| `FLTMGR.SYS` | 0.0 |
| `vgk.sys` | 0.0 |
| `dxgmms2.sys` | 0.0 |
| `WdFilter.sys` | 0.0 |
| `win32kfull.sys` | 0.0 |
| `ucrtbase.dll` | 0.0 |
| `Ntfs.sys` | 0.0 |

**`x86-warm` vs `x64-warm`** - biggest movers:

| key | Δ median cpu ms |
|---|---:|
| `ntoskrnl.exe` | +2.0 |
| `ntdll.dll` | +1.6 |

### Wait time by readying process

**x64-warm** - top 10:

| key | median wait ms |
|---|---:|
| `7z.exe` | 7.8 |
| `conhost.exe` | 7.5 |
| `MsMpEng.exe` | 2.2 |
| `powershell.exe` | 0.7 |
| `csrss.exe` | 0.0 |

**x86-warm** - top 10:

| key | median wait ms |
|---|---:|
| `7z.exe` | 10.1 |
| `conhost.exe` | 7.6 |
| `MsMpEng.exe` | 2.2 |
| `powershell.exe` | 0.7 |
| `csrss.exe` | 0.0 |

