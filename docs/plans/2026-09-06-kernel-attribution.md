# Addendum — kernel-level attribution and cross-device methodology

Written by the planning session on 2026-09-06, against the repo at `46477d8`.
**Status: specification only. No hands-on work is released yet.**
Merge into `2026-09-06-launchlab.md` when adopted; this file is the delta, not a replacement.

## Why

The role is **Hardware Engineering Enablement**. The customer is an OEM or a silicon
partner, and the daily loop is: their device regressed → capture on hardware you do not
control → prove *which component* costs the time → they push back → your numbers have to
hold → and it has to run across many devices and many builds.

Measured against that, the repo has two gaps that are not about "going lower" in the
abstract:

1. **Attribution stops at the user-mode process boundary.** `analyze` filters to the
   measured pid. In this job the answer is usually "your driver", and an analyzer that
   cannot cross into kernel mode cannot produce that answer.
2. **One device, no dispute.** Everything so far measures our own workload on our own
   machine and believes its own numbers.

Everything below closes one of those two.

---

## 0. Already built, not yet claimed — do this first, it costs nothing

`run.ps1` detects that the VM has no virtualised PMU, falls back to
`launchlab-nosampling.wprp`, and records `cpu_sampling: false` in `env.json`.

That is **capability detection and graceful degradation across heterogeneous hardware**,
which is exactly the fleet problem, and right now it is buried as an implementation note in
the plan file. Promote it to a README section:

> The same measurement suite cannot assume the same instruments are available on every
> device. The harness probes what the machine can record, degrades to a reduced profile,
> and marks the resulting rows so an analysis never silently compares a sampled run against
> an unsampled one.

**Acceptance:** a README section saying this, and `report.md` visibly refusing to emit
CPU-by-module for a run whose `env.json` says `cpu_sampling: false` (rather than emitting
an empty table that reads like "no CPU was used").

Cost: ~20 minutes. Highest value-per-minute item in this document.

---

## 1. Physical-machine run — the prerequisite for everything else

The physical x64 Windows 11 has never been run. It is the only machine that can sample the
CPU, so it gates items 2 and 4.

Run the existing Stage 1 cold/warm suite there unchanged, with `launchlab.wprp`
(the sampling profile). Nothing new to write.

**Acceptance:** `results/` from the physical machine with `cpu_sampling: true` in
`env.json`, and a non-empty CPU-by-module table in `report.md`.

---

## 2. `analyze --scope system` — driver-level attribution

Today every CPU sample is filtered to the target pid. Add a scope switch that drops the
filter and buckets differently.

For each `ICpuSample`, classify before bucketing:

| class | test |
|---|---|
| ISR | `sample.IsExecutingInterruptServicingRoutine` |
| DPC | `sample.IsExecutingDeferredProcedureCall` |
| normal | neither |

Both fields are already on `ICpuSample` and are verified present in 1.12.10. Within each
class, bucket by `sample.Image?.FileName ?? "<unknown>"` — for kernel-mode samples that is
the driver image (`ntoskrnl.exe`, `*.sys`), and **no symbols are needed**, same as today.

Weight by `sample.Weight` summed, never by sample count: the sampling interval is not
guaranteed uniform and a count silently becomes wrong if the interval is ever changed.

**Do not touch `runs.csv`.** `compare` depends on that schema and it is the regression
gate. Write a separate `<etl>.system.json` per trace and have `report` read it. One row per
run stays one row per run.

**Acceptance:**
- On a physical-machine ETL, `--scope system` lists at least one `.sys` module and a
  non-zero DPC or ISR bucket.
- If DPC/ISR come back empty, the capture is missing keywords → item 3.

**Trap:** DPC/ISR magnitudes inside a VM are virtualisation artefacts, not device
behaviour. Every driver-level conclusion is a **physical-machine-only** claim and the
README must say so. Do not let a VM number into a driver table.

---

## 3. Add `DPC` and `Interrupt` keywords to `launchlab.wprp`

The current profile is deliberately narrow and does not include them, so DPC durations are
not in the trace at all. Add to the `SystemProvider` keyword list:

```xml
<Keyword Value="DPC"/>
<Keyword Value="Interrupt"/>
```

Verify the exact keyword spellings against the WPR `SystemProvider` keyword table on
learn.microsoft.com before assuming — a wrong keyword makes `wpr -start` fail outright,
which is at least a loud failure.

Two things worth testing while in there, both one line and one run:

- **Do these keywords work in the VM?** They are unrelated to `SampledProfile`, so the
  no-PMU failure may not apply. If they work, the VM gains DPC-event data even though it
  can never sample. (Still not credible as *device* behaviour — see the trap above.)
- **`<Stack Value="CSwitch"/>`** — stack walking on context switch does not depend on the
  PMU either. If it works in the VM it gives a weak but non-zero form of module attribution
  where sampling is impossible. Worth 10 minutes to find out; drop it if it fails.

Keep the additions out of `launchlab-nosampling.wprp` until the VM test above says they
work there.

**Trap:** every added keyword is overhead charged to the thing being measured, and this
profile starts and stops once per launch. Re-run the existing tracing-overhead measurement
after the change and record the new number. If DPC/Interrupt measurably move it, split
them into a third profile used only for item 4 rather than paying for them on every
startup run.

---

## 4. DPC duration ↔ observed wake latency (physical machine only)

The signature hardware-enablement failure: a driver holds a DPC too long, and the user
feels it as a glitch. This experiment produces that finding in the shape a partner would
receive it.

**Probe** — extend `Spike.cs` with `--wake-latency <seconds>`:

- Loop: read `Stopwatch.GetTimestamp()`, `Thread.Sleep(1)`, read again, record the delta.
- `Stopwatch`, not `DateTime` — QPC resolution is the whole point.
- Raise the thread to `ThreadPriority.Highest` so the latency being measured is kernel
  delay, not contention with ordinary threads.
- Emit `wake-latency.csv`: `iteration, qpc_ticks, latency_us`.

**Load** — nothing new to write. A large-file copy loop drives the storage driver;
`spike --write --mb N` already exists and flushes to the device. Network load via a
download loop is the alternative if storage DPCs turn out to be uninteresting.

**Two arms, not event alignment.** Record idle and under-load, and compare distributions:

| | wake latency p50 / p99 / max | per-driver DPC p99 / max |
|---|---|---|
| idle | | |
| under load | | |

The claim is then statistical over an interval — "under load, wake-latency p99 went from
X to Y µs while `<driver>.sys` DPC p99 was Z µs" — which needs no clock correlation and
is enough to be actionable.

**Deliberately deferred:** per-event alignment (probe emits an `EventSource` event on each
outlier, so ETW timestamps both sides and the outlier can be matched to the exact DPC
executing at that instant). It is the stronger claim and it hits the JD's custom-
instrumentation angle, but it needs an `EventCollector`/`EventProvider` section in the
`.wprp` and the EventSource GUID derived from the provider name. Build it only if the
two-arm result is not convincing on its own.

**Uncertain, check before building:** whether TraceProcessing 1.12.10 exposes DPC/ISR
*events* (individual durations) as a first-class data source, or only DPC/ISR-flagged CPU
samples. If only samples, per-driver DPC *share* is available but per-DPC *duration* is
approximate — say so in the README and cross-validate against WPA's DPC/ISR table, which
is a screenshot that needs taking anyway.

---

## 5. Cross-device noise floor — the fleet methodology section

Zero new code. Run the identical warm suite on both machines and put the two noise floors
side by side. `compare` already derives its threshold from the baseline's MAD, so the
numbers come out of runs that have to happen anyway.

The conclusion is the deliverable:

> A fixed regression threshold expressed in milliseconds false-positives on the quiet
> device and misses real regressions on the noisy one. The gate has to be expressed in
> units of each device's own noise floor.

Two things this buys beyond the sentence:

- It converts the VM's jitter from a liability disclosed in "known limitations" into the
  data point that makes the argument.
- Combined with item 0, it answers "how would you run this across partner hardware?" with
  a demonstrated answer rather than an assertion.

**Acceptance:** one table in the README with both machines' median / MAD / CV% / derived
threshold, and that conclusion stated as a methodology finding.

---

## 6. Stage 3 rewrite — boot as an OEM-shaped deliverable

Keep the existing mechanism (`-boottrace`, snapshot revert, event 27231 as the anchor).
Change what comes out of it. Instead of a single boot number, produce the breakdown a
partner actually receives:

```
firmware handoff → driver init → service start → desktop ready
```

- **Per-driver init cost**: `Loader` image-load events, filtered to `*.sys`, ranked by load
  time within the boot window. This is the table an OEM gets sent.
- **Per-service start cost**: SCM events, or WPA's Services table for cross-validation.
- **Control arm**: disable one service, reboot, re-measure. That is the original Stage 3
  before/after, with the object changed from "a startup item" to "a system service".

**Acceptance:** the four-phase table, a per-driver ranking, and one before/after pair.

---

## 7. Delete Stage 4

The storage micro-benchmark was already the weakest link — least connected to the
candidate's background — and item 4 now covers storage latency better, with a kernel-level
cause attached. Removing it pays for item 4 outright.

---

## Consequence for the README headline — superseded by measurement, 2026-09-06

Two headlines have now been killed by data. Recorded here so neither gets rebuilt.

**Killed #1 — translation CPU.** "X% of the emulation tax is CPU in `xtajit64.dll`" needs
CPU-by-module on arm64, and the only arm64 machine has no PMU.

**Killed #2 — the `.JC` fallback this file originally proposed.** Measured on the VM:
clearing `C:\Windows\XtaCache` (verified effective, 244 files -> 9) produced a
Hodges-Lehmann shift of **-0.8 ms**, 1.7x the noise band, `compare` verdict PASS, with a
negative control on the arm64 arm at -0.3 ms confirming the clearing action itself is
neutral. And `*.JC` never appears in the disk attribution at all: the writes happen in the
XtaCache service, not in the launched process, so a pid-scoped analyzer cannot see them by
design. **Item 2 (`--scope system`) is now the only route to that half of the cost.**

**What the data does support**, and what the README should lead with:

- x64 under Prism is **+14.5 ms slower than native arm64** (median 33.8 vs 19.3, ~1.75x),
  warm, 20 interleaved runs each. Reproduced across a harness change that moved the
  absolute values ~10 ms while the shift held at 14.5-15.5 ms — the stability of the shift
  under a changed instrument is itself the robustness argument.
- **`XtaCache.exe` appears in the x64 arm's wait attribution (median 1.6-3.2 ms) and is
  absent from the arm64 arm.** With no CPU sampling available, CSwitch/ReadyThread still
  named the emulation infrastructure through "who readied this thread".

**Frame the null result as the headline, not as a fallback.** "The intuitive explanation is
wrong, here is the negative control proving the manipulation was neutral, and here is what
the cost actually is" is the shape of the work this role does — partners arrive certain it
is the cache, the thermal state, or driver X, and the value added is the evidence that it
is not.

**Two checks before publishing the null result:**

1. **Is the workload too small to have a translation cost at all?** `7z.exe` with no
   arguments runs ~33 ms and executes very little x64 code; XtaCache populates
   asynchronously after observing execution and may never produce a `.JC` for it. List
   `C:\Windows\XtaCache` after the warm x64 batch and check whether anything for this
   binary exists. If not, the wording is "this workload never populates the cache", not
   "the cache does not matter".
2. **Add a heavier x64 arm** — same binary, more work (`7z b`, or compressing a fixed
   file). Two points on one binary give a slope, separating a fixed startup tax from a
   per-instruction emulation tax, and test the translation hypothesis at the volume where
   it should appear if it exists anywhere.

**Correction to item 2 of this document:** on-CPU time is *not* lost without a PMU. Only
module attribution is. `Analyze.cs` already iterates `ICpuThreadActivity` and reads
`ReadyDuration` and `WaitingDuration` while ignoring `a.Duration`, which is the running
slice and is derived from context switches. Summing it gives exact per-process CPU time on
the VM, which makes the +14.5 ms decomposable into on-CPU + ready + wait on hardware
already in hand — currently only 10-20% of the shift is named.

## Do not change

- **`runs.csv` schema.** `compare` is the regression gate and depends on it. New analyses
  emit new files.
- **The default recording path.** `launchlab.wprp` / `launchlab-nosampling.wprp` selection
  already works and produced real numbers; new keywords go in only after the overhead
  re-measurement in item 3.
- **Never put a number in the README the harness did not produce.** Unchanged.

---

## Suggested order

| # | item | machine | rough cost |
|---|---|---|---|
| 1 | promote capability-detection to a README section (item 0) | none | 20 min |
| 2 | physical-machine Stage 1 run (item 1) | physical | 1 h |
| 3 | `DPC`/`Interrupt` keywords + overhead re-measure (item 3) | physical, then VM test | 40 min |
| 4 | `analyze --scope system` (item 2) | analysis only | 1 h |
| 5 | cross-device noise floor table (item 5) | both, data already in hand | 30 min |
| 6 | headline decision + README rewrite (consequence section) | none | 30 min |
| 7 | wake-latency probe + two-arm DPC experiment (item 4) | physical | 2–3 h |
| 8 | Stage 3 boot breakdown (item 6) | VM | 2–3 h |

Items 1–6 are roughly a half day and raise the repo from "application benchmark" to
"system attribution across heterogeneous devices". Item 7 is the one genuinely new
experiment. Item 8 is the largest and is the first thing to cut if time runs out.
