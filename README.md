# LaunchLab

Measure Windows process-startup responsiveness with ETW, and say **where the time went** —
which module, which file, which process — instead of only how long it took.

In-box `wpr` records one trace per launch against a purpose-built profile. A .NET analyzer
built on the [TraceProcessing SDK](https://www.nuget.org/packages/Microsoft.Windows.EventTracing.Processing.All)
extracts per-run metrics from every trace. A report ranks the contributors and flags the
runs that were not like the others. A `compare` gate turns two runs into a pass/fail with an
exit code.

> **中文摘要**：用 Windows 內建的 `wpr` 錄下每一次啟動的 ETW trace，再用 TraceProcessing SDK
> 程式化地抽出「啟動耗時」與它的組成（實際佔用 CPU 的時間、排程等待、磁碟服務時間、硬缺頁），
> 交錯執行 A/B 以排除漂移，用中位數與 MAD 而非平均與標準差來偵測不穩定的量測。
> 下面每一個數字都是這套工具實際跑出來的。

Everything below was measured with this harness. The environment is disclosed with every
result, and `results/report.md` is generated, never hand-written.

---

## What was measured

**Machine:** Windows 11 Pro ARM64, build 26200.8037, running as a VMware Fusion guest on
Apple silicon — 4 vCPU, 8 GB, SSD, Defender real-time protection **on**, Balanced power plan.
**Workload:** 7-Zip 26.03, the arm64 build and the x64 build of the same release, invoked so
that they start, print usage, and exit. On Windows on Arm the x64 build runs under Prism
emulation; the arm64 build does not. That is the only difference between the two arms.

**Metric:** `startup_ms` = process create → process exit, both timestamps taken from the
kernel process events inside the trace.

### 1. Emulating x64 costs ~15 ms per launch, and the number does not move

| batch | how stdout was captured | tracing | arm64 median | x64 median | Hodges–Lehmann shift |
|---|---|---|---:|---:|---:|
| 1 | temp file | every run | 9.6 ms | 25.1 ms | **+14.9 ms** |
| 2 | pipe | every run | 19.3 ms | 33.8 ms | **+14.6 ms** |
| 3 | pipe | every run | 20.8 ms | 36.3 ms | **+15.5 ms** |
| 4 | pipe | alternating | 18.8 ms | 33.9 ms | **+15.1 ms** |

n = 20 per arm per batch, interleaved A/B/A/B.

The absolute numbers move by 2× across batches — because how the harness captures the
workload's stdout changes what the workload pays, which is itself worth knowing. **The shift
does not**: four independent batches, two different capture mechanisms, two tracing cadences,
14.6–15.5 ms every time. That stability is the result; the absolute medians are not.

### 2. Nine tenths of that gap is time actually executing

Batch 3, medians, n = 20 per arm:

| | arm64 | x64 | Δ |
|---|---:|---:|---:|
| `startup_ms` | 20.8 | 36.3 | **+15.5** |
| `cpu_on_ms` — time on a processor | 10.3 | 24.2 | **+13.9** |
| `ready_ms` — runnable, not scheduled | 0.9 | 1.3 | +0.4 |
| images loaded | 17 | 18 | +1 |

`cpu_on_ms` comes from context-switch data, not from CPU sampling, so it is **exact and needs
no PMU** — which matters, because this machine has no PMU to offer (see §6). The emulation
tax is executing more instructions, not waiting more.

It is not attributable to a module here: that needs sampling, and this environment cannot
sample. The limit is stated rather than estimated.

### 3. The tax scales with how much x64 code runs

Same two binaries, given real work — hashing a fixed 32 MB file (n = 12 per arm):

| workload | arm64 | x64 | shift | `cpu_on_ms` Δ |
|---|---:|---:|---:|---:|
| start and exit | 20.8 ms | 36.3 ms | +15.5 ms | +13.9 ms |
| hash 32 MB | 29.8 ms | 58.9 ms | **+28.7 ms** | **+27.0 ms** |

Two points on the same binary separate a fixed startup tax from a per-work one. This is the
latter: double the executed code, roughly double the tax, with `cpu_on_ms` tracking the shift
at both volumes.

### 4. The obvious explanation is wrong: clearing the translation cache costs nothing

Prism caches its x64→ARM64 translations as `.JC` files in `C:\Windows\XtaCache`, so the
intuitive story is that a cold cache makes the first run expensive. It does not.

| comparison | baseline median | candidate median | shift | vs noise band | verdict |
|---|---:|---:|---:|---:|---|
| x64 warm → x64 cold cache | 33.8 ms | 33.4 ms | −0.8 ms | 1.7× | **PASS** |
| arm64 warm → arm64 cold cache *(control)* | 19.3 ms | 18.9 ms | −0.3 ms | 0.5× | PASS |

The control matters: it shows the act of stopping the service and clearing the directory is
itself neutral, so the null result is not the clearing cancelling the effect. `cpu_on_ms`
agrees — 24.2 ms warm against 24.0 ms cold.

**Why**, probed directly: clear the cache (0 files), run the x64 binary five times, wait, and
9 `.jc` files appear — `MSVCRT.DLL`, `KERNELBASE.DLL`, `GDI32.DLL`, `RPCRT4.DLL`,
`COMBASE.DLL`, `GDI32FULL.DLL`. All of them are the x64 **system DLLs** the process loads.
None is `7z.exe`. They are written **asynchronously by the XtaCache service after the run**,
so the launching process never waits for them, and a launch with a cold cache costs what a
launch with a warm one costs.

Corroborating this from the other direction, the wait attribution names `XtaCache.exe` as a
process that unblocks the measured thread — **only in the x64 arm** (1.6–3.2 ms median),
never in the arm64 arm. The emulation infrastructure is visible in the trace by name, without
any CPU sampling at all.

### 5. The most expensive thing in the experiment was idleness

Trying to price the recorder produced a *negative* overhead — untraced runs were 2–3× slower
than traced ones — in both a batched design and one that alternated tracing run by run. That
is not an observer effect, so the instrument was checked instead of the hypothesis
(wall-clock on both sides, same clock, same code path). n = 15 per arm:

| | arm64 | x64 |
|---|---:|---:|
| traced, 2 s between runs | 20.0 ms | 35.8 ms |
| **untraced, 0 s between runs** | **19.8 ms** | **35.3 ms** |
| untraced, 2 s between runs | 55.8 ms | 78.6 ms |

**Recording with this profile costs essentially nothing** (19.8 → 20.0 ms). What costs 36–43 ms
is letting the machine sit idle for two seconds first. The recorder's own work between runs
kept the processor busy and hid the wake-up cost — which is exactly why a traced batch cannot
be compared against an untraced one.

The settle time added *for* measurement rigour was the largest single confound in the
experiment, and it is more than twice the effect under study. Interleaving is what saves the
comparison: every arm pays the same idle.

### 6. The harness knows what the machine cannot measure

`wpr -start` fails with `0x80070032` for any profile containing `SampledProfile` on this
guest — no PMU is exposed, so `GeneralProfile`, `CPU`, `Registry`, `Minifilter` and `Power`
are all unavailable, while `ProcessThread`, `Loader`, `CSwitch`, `ReadyThread`, `HardFaults`
and `DiskIO` work.

The same suite cannot assume the same instruments exist on every device. So the harness probes
what the machine will accept, degrades to `launchlab-nosampling.wprp`, records
`cpu_sampling: false` in `env.json`, and the report **says the CPU-by-module breakdown is
absent rather than printing an empty table that reads like "no CPU was used"**. A run is
marked with what it could measure, so an analysis never silently compares a sampled run
against an unsampled one.

### 7. Does the analyzer actually work? A known cost, injected and recovered

The harness measures itself: `spike` injects a **known** cost *inside the measured process* —
work done by the runner would be charged to the runner and correctly ignored, so the check
would pass by measuring nothing. Injected: 120 ms of CPU spin plus a 32 MB write flushed to
the device. n = 12 per arm.

| | baseline | injected | Δ |
|---|---:|---:|---:|
| `startup_ms` | 38.9 ms | 192.0 ms | **+153.1** |
| `cpu_on_ms` | 33.2 ms | 175.1 ms | **+141.9** |
| `disk_service_ms` | 0.0 ms | 10.6 ms | **+10.6** |
| disk I/Os | 0 | 43 | +43 |

The gate called it at **310× the baseline noise band**, ~93% of the cost landed in on-CPU
time where the spin was, and the disk half was attributed by name to `C:\lab\spike.bin`
(10.3 ms) — the exact file written. Both injected costs stayed separable.

---

## How it works

```
scripts/run.ps1            per run: set cache state, wpr -start, launch, wpr -stop
  └── results/*.etl        one trace per launch, plus a sidecar naming config/run/pid
launchlab analyze <dir>    ETL -> runs.csv (one row per run) + attrib.csv (long-form)
launchlab report  <dir>    -> report.md + summary.json
launchlab compare a.csv b.csv   regression gate, exit code 0 or 1
launchlab spike            negative-control workload
```

Per run, `runs.csv` carries `startup_ms`, `wall_ms`, `cpu_on_ms`, `cpu_ms`, `ready_ms`,
`wait_ms`, `disk_service_ms`, `disk_ios`, `disk_mb`, `hardfaults`, `hardfault_io_ms`,
`images_loaded` and `disk_qd_median`.

`attrib.csv` names the participants, because "disk cost 200 ms" is not something an owner can
act on and "200 ms of it was reading *this file*" is:

| kind | key | answers |
|---|---|---|
| `module` | image name | which module burned the CPU (needs sampling) |
| `disk_read_file` / `disk_write_file` | file path | which file the storage time went to |
| `hardfault_file` | file path | what was being paged in |
| `readying` | process image | who unblocked the waiting thread — WPA's Wait Analysis, in code |

Module attribution uses `ICpuSample.Image`, the image of the sampled instruction, so it needs
**no symbol server and no PDBs**. Function-level analysis is what WPA is for.

**What `startup_ms` does not cover:** GUI first frame or input readiness. A run-to-exit
console workload makes the whole measurement *be* process startup — loader, DLL mapping,
page faults, first execution of the code — with no custom instrumentation needed to detect
"the window is ready". A GUI-ready metric needs a marker written into the trace.

**Components overlap and are summed across threads.** Disk service time is served while a
thread waits; `wait_ms` on a multi-threaded workload can exceed the wall-clock `startup_ms`.
They rank contributors. They are not a budget, and the report says so where the numbers are.

## Methodology

- **Interleaved A/B/A/B**, never one config in a block. Thermal drift, background work, host
  load and — as §5 shows — idle state then hit every arm equally.
- **Median and MAD, not mean and σ.** One 3× run poisons a standard deviation, and startup
  measurements always have one.
- **A flaky run must clear two bars**: more than 3 MAD from its config median *and* more than
  10% off it. On a tight distribution 3 MAD is a fraction of a millisecond, which flagged a
  third of a healthy run set until the second bar was added.
- **Hodges–Lehmann shift** for A/B, so the comparison does not assume normality.
- **The gate scales by the baseline's own noise**, `1.4826 × MAD`, not by a standard
  deviation — for the same reason.
- **Two independent clocks.** Every run is also timed by the harness stopwatch; the report
  prints it beside the trace-derived time. A gap means the analyzer is measuring the wrong thing.
- **The harness adds no I/O to its subject.** An early version redirected the workload's
  stdout to a temp file — and those writes showed up in the workload's own disk attribution.
  Output goes through pipes now.
- **Refuse invalid designs rather than produce misleading numbers.** `coldxta` clears a
  machine-wide cache, so the runner refuses to interleave it with a warm arm; compare the two
  batches with `compare` instead.
- **Environment is disclosed, not tidied up.** `env.json` records OS build and UBR, CPU, RAM,
  hypervisor, disk media, AC/battery, Defender state, power plan, the recording profile
  actually used, and the PE machine type of every workload binary. Defender stayed on.

## Quick start

Windows 10/11, .NET 8 SDK, elevated PowerShell (`wpr` requires it). No ADK needed to collect —
`wpr.exe` ships in Windows.

```powershell
# Collect: 20 launches of each binary, interleaved
.\scripts\run.ps1 -Apps 'arm64=C:\7z-arm64\7z.exe','x64=C:\7z-x64\7z.exe' -Mode warm -N 20

dotnet run --project src\LaunchLab -- analyze results\warm
dotnet run --project src\LaunchLab -- report  results\warm

# Regression gate: exit code 0 = within the baseline's noise, 1 = regression
dotnet run --project src\LaunchLab -- compare baseline\runs.csv candidate\runs.csv

# Statistics self-check, no test framework
dotnet run --project src\LaunchLab -- selftest
```

`-Mode coldfile` copies the application fresh per run; `-Mode coldxta` clears the Prism
translation cache per run; `-NoTrace` runs without the recorder; `-AlternateTracing` traces
every other run.

## Known limitations

- `startup_ms` measures a run-to-exit console workload, not GUI readiness.
- Components overlap and are summed across threads; treat them as a ranking.
- Every number here comes from one virtual machine. Absolute values are specific to it —
  §1 shows they move with the harness's own choices. Compare arms measured on the same
  machine in the same session; never absolute numbers across machines.
- CPU-by-module is unavailable on this machine (§6), so the emulation tax is quantified but
  not attributed to a module. That needs physical ARM64 hardware.
- Not yet done: cross-validation screenshots against WPA on the same traces, a run on
  physical x64 hardware, and boot-trace analysis.

## Layout

```
scripts/run.ps1                    scenario runner: cache state, wpr, one ETL + sidecar per run
scripts/launchlab.wprp             recording profile: only the keywords the analyzer reads
scripts/launchlab-nosampling.wprp  fallback for machines that cannot sample the CPU
src/LaunchLab/
  Analyze.cs   ETL -> per-run metrics and attribution (TraceProcessing SDK)
  Stats.cs     median, MAD, p95, CV, Hodges-Lehmann, outlier detection, self-check
  Report.cs    runs.csv + attrib.csv -> report.md + summary.json
  Compare.cs   regression gate with an exit code
  Spike.cs     negative-control workload
docs/plans/                        the staged build plan this repo follows
```
