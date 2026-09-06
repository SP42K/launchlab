# LaunchLab

Measure Windows process-startup responsiveness with ETW, then say **where the time went** —
not just how long it took.

`wpr` (in-box) records one trace per run. A .NET analyzer built on the
[TraceProcessing SDK](https://www.nuget.org/packages/Microsoft.Windows.EventTracing.Processing.All)
pulls per-run metrics out of every trace. A report ranks the contributors, flags the flaky runs,
and attributes CPU cost down to the module.

> **中文摘要**：用 Windows 內建的 `wpr` 錄下每一次啟動的 ETW trace，再用 TraceProcessing SDK
> 程式化地抽出「啟動耗時」與它的組成（CPU、排程等待、磁碟服務時間、硬缺頁），
> 交錯執行 A/B 以排除漂移，用中位數與 MAD 而非平均與標準差來偵測不穩定的量測，
> 最後產生一份會指出「差異主要來自哪裡」的報告。

---

## What it measures

**`startup_ms` = process create → process exit**, both timestamps taken from the kernel process
events inside the trace.

The workload is an app invoked so that it starts, does nothing, and exits (`7z.exe` with no
arguments prints its usage and quits). That choice is deliberate: it makes the whole measurement
*be* process startup — image loading, DLL mapping, page faults, first execution of the code —
with no custom instrumentation needed to detect "the window is ready".

**What it does not cover:** GUI first frame, input readiness, or anything an app does after
its startup path. A GUI-ready metric needs a marker written into the trace; that is a later stage,
not this one.

Alongside it, per run:

| metric | source | what it tells you |
|---|---|---|
| `cpu_ms`, and per-module breakdown | `UseCpuSamplingData()` | where the CPU actually went |
| `ready_ms` | `UseCpuSchedulingData()` | time runnable but not scheduled |
| `wait_ms` | `UseCpuSchedulingData()` | time blocked |
| `disk_service_ms`, `disk_ios`, `disk_mb` | `UseDiskIOData()` | storage cost |
| `hardfaults`, `hardfault_io_ms` | `UseHardFaults()` | paging cost |
| `images_loaded` | `UseProcesses()` | loader work |

These components **overlap** — disk service time is served while a thread waits — so they rank
contributors rather than summing to `startup_ms`. The report states this where the numbers are.

Module attribution uses `ICpuSample.Image`, the image of the sampled instruction, so it needs
**no symbol server and no PDBs**. Function-level analysis is what WPA is for.

`attrib.csv` goes one level further and names the participants, because "disk cost 200 ms" is not
something an owner can act on and "200 ms of it was reading *this file*" is:

| kind | key | answers |
|---|---|---|
| `module` | image name | which module burned the CPU |
| `disk_read_file` / `disk_write_file` | file path | which file the storage time went to |
| `hardfault_file` | file path | what was being paged in |
| `readying` | process image | who unblocked the waiting thread (WPA's Wait Analysis, in code) |

## Methodology

The parts that separate this from a stopwatch loop:

- **Interleaved A/B/A/B**, never one config in a block. Thermal drift, background work and
  (in a VM) host load then hit every arm equally.
- **N = 20 per config** by default, plus an untraced warm-up launch per app.
- **Median and MAD, not mean and σ.** A single 3× run poisons a standard deviation, and startup
  measurements always have one. A run further than 3 MAD from its config median is flagged flaky,
  and the report attributes *that run's* delta to a component.
- **Hodges–Lehmann shift estimate** for A/B, so the comparison does not assume normality.
- **Two independent clocks.** The harness records a stopwatch time per run; the report prints it
  next to the trace-derived time. A gap between them means the analyzer is measuring the wrong thing.
- **Environment is disclosed, not tidied up.** `env.json` records OS build, CPU, RAM, VM or not,
  Defender real-time state and the active power plan, and the report reproduces it.
- **Cold state is produced, not assumed.** A cold run launches a freshly copied application
  directory, so the binary is not in the file cache — and, on Windows on Arm, has no cached x64
  translation yet. `-HardCold` additionally stops the `XtaCache` service and clears
  `C:\Windows\XtaCache`.

## Quick start

Windows 11, .NET 8 SDK, elevated PowerShell (`wpr` requires it). No ADK needed to collect — 
`wpr.exe` ships in Windows.

```powershell
# 1. Collect: 20 cold + 20 warm launches, interleaved
.\scripts\run.ps1 -Apps '7z=C:\Program Files\7-Zip\7z.exe' -Mode both -N 20

# 2. Extract per-run metrics from every trace
dotnet run --project src\LaunchLab -- analyze results

# 3. Build the report
dotnet run --project src\LaunchLab -- report results
```

Outputs land in `results/`: `runs.csv` (one row per run), `attrib.csv` (long-form attribution),
`report.md`, `summary.json`.

Comparing native ARM64 against x64-under-emulation on a Windows on Arm machine is the same
pipeline with two apps:

```powershell
.\scripts\run.ps1 -Apps 'arm64=C:\7z-arm64\7z.exe','x64=C:\7z-x64\7z.exe' -Mode both -N 20
```

Sanity check for the statistics, no test framework required:

```powershell
dotnet run --project src\LaunchLab -- selftest
```

## Results

<!-- FILL AFTER THE FIRST REAL RUN. Do not write numbers here that the harness has not produced. -->

_Not yet run. `results/report.md` will hold the generated tables; the headline finding goes here._

## Cross-validation with WPA

<!-- FILL: open the same .etl in Windows Performance Analyzer, frame the same interval, and
     screenshot CPU Usage (Sampled), Disk Usage and Wait Analysis next to the analyzer's numbers. -->

_Pending._ The analyzer is only trustworthy if its numbers match what WPA shows for the same
interval of the same trace. That comparison belongs here, with screenshots.

## Known limitations

- `startup_ms` measures a run-to-exit console workload, not GUI readiness.
- Components overlap; treat them as a ranking, not a budget.
- Inside a VM, timestamps come from a virtualized clock. Compare arms measured on the same
  machine in the same session — never absolute numbers across machines.
- Windows Defender scanning a freshly copied binary is part of a cold launch's real cost.
  It is disclosed rather than disabled.

## Layout

```
scripts/run.ps1      scenario runner: cache state, wpr start/stop, one ETL + sidecar per run
src/LaunchLab/       analyze | report | selftest
  Analyze.cs         ETL -> per-run metrics (TraceProcessing SDK)
  Stats.cs           median, MAD, p95, CV, Hodges-Lehmann, outlier detection, self-check
  Report.cs          runs.csv -> report.md + summary.json
results/             traces, CSVs and the generated report
docs/plans/          the staged build plan this repo follows
```
