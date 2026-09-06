# LaunchLab — staged build plan

Self-contained: a fresh session should be able to pick this up from the file alone.

## Why this exists

Portfolio piece for a **Microsoft WECE / Hardware Engineering Enablement, Senior Software
Engineer** application (Windows startup and responsiveness performance). The job is
ETW/WPR/WPA capture → programmatic analysis with the TraceProcessing SDK → defined metrics
and measurement methodology → diagnosing non-deterministic results → actionable root cause,
with boot flow, memory/storage/IO latency, ADK/WAT and WPA plugins as adjacent skills.

Built as a ladder: **the repo is presentable at the end of every stage**, because the job
posting may close at any time.

## Environments

| | |
|---|---|
| arm64 VM | VMware Fusion, Windows 11 Pro 26200.8037 ARM64, 4 vCPU / 8 GB / SSD. `ssh win11` from the Mac, key-only, **and that session is already elevated**, so the whole harness drives from the Mac. Snapshot `clean-ssh-ok` is the clean restore point. |
| physical | Windows 11 x64. Provides the native-x64 baseline and, unlike the VM, can sample the CPU. |
| Mac | Build and analysis host. `brew install dotnet` (SDK 10) builds the net8.0 project; run it with `DOTNET_ROLL_FORWARD=LatestMajor`. |

**Boot the VM from the Fusion UI, never `vmrun start -nogui`** — the UI cannot attach to a
VM started headless. `vmrun` works fine as a remote control for a UI-started VM.

## Hard-won environment facts

- **The VM cannot record CPU samples.** `wpr -start` returns `0x80070032` (not supported) for
  any profile containing the `SampledProfile` keyword — GeneralProfile, CPU, Registry,
  Minifilter and Power all fail for this reason. `ProcessThread`, `Loader`, `CSwitch`,
  `ReadyThread`, `HardFaults` and `DiskIO` all work. Cause: no PMU is exposed to the guest.
  The harness detects this and falls back to `scripts/launchlab-nosampling.wprp`, recording
  `cpu_sampling: false` in `env.json`. **CPU-by-module attribution therefore has to come from
  the physical machine.**
- **XtaCache keys on binary content, not path.** Copying an x64 binary to a fresh directory
  does *not* give it a cold translation cache — measured, `coldfile` x64 came out the same as
  `warm`. Only clearing `C:\Windows\XtaCache` (mode `coldxta`) produces a cold emulation run.
- **XtaCache is machine-wide**, so `coldxta` cannot be interleaved with a warm arm without
  stripping the warm arm's cache too. The runner refuses that combination; compare the two
  batches with `launchlab compare` instead.
- Windows PowerShell 5.1 turns *anything* a native command writes to stderr into a
  terminating error under `ErrorActionPreference = Stop`. All `wpr` calls go through
  `Invoke-Wpr`, which handles the streams and returns the exit code.
- `IsInRole('Administrator')` as a string is always false; use the
  `[Security.Principal.WindowsBuiltInRole]::Administrator` enum.

## Status

- **Stage 0/1 — pipeline: done.** `run.ps1` → one ETL per launch → `analyze` → `report`.
  Metrics, robust statistics, flaky detection with per-run attribution.
- **Stage 2 — emulation tax: measured on the VM.** arm64 native vs x64 under Prism, warm,
  20 interleaved runs each, plus a cold-XtaCache batch. Numbers live in the README.
- **Cross-cutting, done:** environment disclosure, file- and process-level attribution,
  `compare` regression gate, `spike` negative control, tracing-overhead measurement.
- **Not done:** WPA cross-validation screenshots; the physical-machine run (which is where
  CPU-by-module can come from); Stage 3 boot trace; Stage 4 storage micro-benchmark;
  Stage 5 AI-assisted RCA and a WPA add-in.

## Remaining stages

### Stage 3 — boot flow

`wpr -boottrace -addboot <profile> -filemode`, reboot, `wpr -boottrace -stopboot boot.etl`.
`Microsoft-Windows-Shell-Core` (GUID `30336ed4-e327-447c-9de0-51b652c86108`) event **27231**
is `ExplorerStartToDesktopReady` — the boot-time anchor the official TraceProcessing
`BootTimeDiff` sample uses.

The VM earns its keep here: `vmrun revertToSnapshot <vmx> clean-ssh-ok` then
`vmrun start <vmx> gui`, poll SSH until it answers, collect. That is a bit-identical
initial state on every iteration, which no physical machine can offer — worth writing up as
a methodology section. Budget 60–90 s per iteration.

### Stage 4 — storage I/O latency

Extend `spike` into a sweep over queue depth × block size × sequential/random, and compare
the latency the application sees against the ETW disk service time. The gap is queueing and
filesystem overhead.

### Stage 5 — optional

- AI-assisted RCA: feed `summary.json` (already emitted) to `claude -p` for hypotheses and a
  "what to capture next" suggestion. The boundary is the point: the model reasons over
  metrics a program extracted deterministically, never over raw ETL.
- A WPA add-in via `Microsoft.Performance.SDK` surfacing `summary.json` as a WPA table.

## Rules that are not negotiable

- **Never put a number in the README the harness did not produce.**
- Disclose the measurement environment; never quietly disable Defender and call it a result.
- Components (`cpu_ms`, `ready_ms`, `wait_ms`, `disk_service_ms`, `hardfault_io_ms`) overlap
  and are summed across threads. They rank contributors; they are not a budget.
