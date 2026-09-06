# LaunchLab report

Generated 2026-09-06 16:53 from `runs.csv` (24 runs, 2 configs).

## Measurement environment

- **generated_at**: 2026-09-06T16:23:09
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
- **app_args**: h C:\lab\spike.bin
- **runs_per_config**: 12
- **settle_seconds**: 2
- **interleaved**: True
- **alternate_tracing**: False
- **configs**: x64-warm <- C:\7z-x64\7z.exe [warm, x64]; x86-warm <- C:\7z-x86\7z.exe [warm, x86]

## Startup time per config

`startup_ms` = process create -> process exit, taken from the kernel process events in the trace.

| config | n | median ms | MAD ms | p95 ms | min ms | max ms | CV % | flaky runs |
|---|---:|---:|---:|---:|---:|---:|---:|---|
| x64-warm | 12 | 34.0 | 0.8 | 63.5 | 31.5 | 63.5 | 24.0 | run 1 |
| x86-warm | 12 | 36.9 | 1.1 | 77.6 | 34.9 | 77.6 | 29.7 | run 1 |

Cross-check against the harness stopwatch (an independent clock; a large gap means the analyzer is measuring the wrong thing):

| config | median startup_ms (trace) | median wall_ms (stopwatch) |
|---|---:|---:|
| x64-warm | 34.0 | 35.8 |
| x86-warm | 36.9 | 38.7 |

## Where the time goes

Medians per config. **These components overlap** (disk service time is served while the thread waits), so they rank contributors - they are not an additive budget of `startup_ms`. `ready_ms` and `wait_ms` are summed over every thread in the process, so on a multi-threaded workload they can legitimately exceed the wall-clock `startup_ms`.

| config | cpu_on_ms | cpu_ms | ready_ms | wait_ms | disk_service_ms | hardfault_io_ms | disk_ios | hardfaults | images_loaded |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| x64-warm | 24.9 | 24.2 | 0.6 | 66.3 | 0.0 | 0.0 | 0.0 | 0.0 | 18.0 |
| x86-warm | 28.0 | 28.0 | 0.3 | 70.1 | 0.0 | 0.0 | 0.0 | 0.0 | 23.0 |

## Comparison against `x64-warm`

| config | Δ median ms | ratio | Hodges-Lehmann shift ms | biggest component mover |
|---|---:|---:|---:|---|
| x86-warm | 2.9 | 1.1x | 3.0 | `cpu_ms` +3.8 ms |

## Flaky runs

A run is flagged when it sits further than 3 MAD from its config median **and** more than 10% off it. The second bar matters: on a tight distribution 3 MAD can be a fraction of a millisecond, and a run nobody would call flaky would be flagged. The attribution is that run's component deltas against the same config's medians.

- **x64-warm run 1**: 63.5 ms, +29.5 ms vs median. Largest mover `wait_ms` +96.5 ms (~327.6% of the delta).
- **x86-warm run 1**: 77.6 ms, +40.7 ms vs median. Largest mover `wait_ms` +137.4 ms (~337.4% of the delta).

## Attribution

Medians across the runs of each config. A key missing from a run counts as zero for that run, so a cost that only appears occasionally does not masquerade as a typical one.

### CPU by module

**x64-warm** - top 10:

| key | median cpu ms |
|---|---:|
| `ntoskrnl.exe` | 12.0 |
| `7z.dll` | 8.0 |
| `ntdll.dll` | 2.5 |
| `vgk.sys` | 0.0 |
| `dxgkrnl.sys` | 0.0 |
| `cldflt.sys` | 0.0 |
| `Ntfs.sys` | 0.0 |
| `FLTMGR.SYS` | 0.0 |
| `KslD.sys` | 0.0 |
| `advapi32.dll` | 0.0 |

**x86-warm** - top 10:

| key | median cpu ms |
|---|---:|
| `ntoskrnl.exe` | 13.0 |
| `7z.dll` | 7.0 |
| `ntdll.dll` | 5.0 |
| `vgk.sys` | 0.5 |
| `FLTMGR.SYS` | 0.0 |
| `WdFilter.sys` | 0.0 |
| `Ntfs.sys` | 0.0 |
| `combase.dll` | 0.0 |
| `7z.exe` | 0.0 |
| `KernelBase.dll` | 0.0 |

**`x86-warm` vs `x64-warm`** - biggest movers:

| key | Δ median cpu ms |
|---|---:|
| `ntdll.dll` | +2.5 |
| `7z.dll` | -1.0 |
| `ntoskrnl.exe` | +1.0 |
| `vgk.sys` | +0.5 |

### Wait time by readying process

**x64-warm** - top 10:

| key | median wait ms |
|---|---:|
| `7z.exe` | 55.9 |
| `conhost.exe` | 7.3 |
| `MsMpEng.exe` | 2.2 |
| `powershell.exe` | 0.7 |
| `lsass.exe` | 0.3 |
| `csrss.exe` | 0.0 |

**x86-warm** - top 10:

| key | median wait ms |
|---|---:|
| `7z.exe` | 59.6 |
| `conhost.exe` | 7.2 |
| `MsMpEng.exe` | 2.2 |
| `powershell.exe` | 0.7 |
| `lsass.exe` | 0.3 |
| `csrss.exe` | 0.0 |
| `SearchProtocolHost.exe` | 0.0 |

