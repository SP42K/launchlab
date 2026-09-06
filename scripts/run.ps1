<#
.SYNOPSIS
    Runs a startup-time scenario N times per config under WPR, one ETL per run.

.DESCRIPTION
    A config is (app x cache state). Cache state is either:
      warm - the same copy of the app is launched every time
      cold - a fresh copy of the app directory is made for each run, so the file is
             not in the file cache and, on Windows on Arm, has no cached x64 translation
             in C:\Windows\XtaCache yet

    Configs are interleaved (A,B,A,B,...) rather than run in blocks, so thermal drift,
    background work and host load hit every arm equally.

.EXAMPLE
    # Stage 1, physical Windows 11: cold vs warm launch of 7-Zip
    .\run.ps1 -Apps '7z=C:\Program Files\7-Zip\7z.exe' -Mode both -N 20

.EXAMPLE
    # Stage 2, Windows 11 on Arm: native arm64 vs x64 under Prism emulation
    .\run.ps1 -Apps 'arm64=C:\7z-arm64\7z.exe','x64=C:\7z-x64\7z.exe' -Mode both -N 20
#>
[CmdletBinding()]
param(
    # One or more "label=path" entries naming the executables to compare.
    [string[]]$Apps = @('7z=C:\Program Files\7-Zip\7z.exe'),

    # warm     - one fixed copy of the app, launched repeatedly
    # coldfile - a fresh copy of the app directory per run: cold file cache for the binary
    # coldxta  - fixed copy, but C:\Windows\XtaCache is cleared before every run, so an
    #            emulated x64 binary has to be translated again (Windows on Arm only)
    [ValidateSet('warm', 'coldfile', 'coldxta')]
    [string[]]$Mode = @('warm', 'coldfile'),

    [int]$N = 20,

    [string]$OutDir = (Join-Path $PSScriptRoot '..\results'),

    # Arguments passed to the app. The default (none) makes 7z print its usage and exit,
    # which keeps the measurement to process startup and nothing else.
    [string]$AppArgs = '',

    [int]$SettleSeconds = 3,

    # Run the same scenario without starting WPR, to measure what the tracing itself costs.
    # Produces no traces; appends wall-clock times to overhead.csv for the report to compare.
    [switch]$NoTrace,

    # Trace every other run instead of every run. Comparing a traced batch against an
    # untraced batch confounds the recorder's cost with whatever else differs between two
    # batches - on a virtual machine, the idle state the recorder itself keeps the vCPU out
    # of. Alternating within one loop removes the batch-level difference.
    [switch]$AlternateTracing,

    # Recording profile. The default asks for CPU sampling; if the machine refuses it
    # (virtual machines commonly have no virtualised PMU) the runner falls back to the
    # no-sampling profile and records that fact in env.json rather than failing.
    [string]$WprProfile = (Join-Path $PSScriptRoot 'launchlab.wprp'),
    [string]$WprProfileFallback = (Join-Path $PSScriptRoot 'launchlab-nosampling.wprp')
)

$ErrorActionPreference = 'Stop'
# PowerShell 7.4+ turns a non-zero native exit code into a terminating error under
# ErrorActionPreference=Stop. wpr -cancel legitimately returns non-zero when no session
# is running, so exit codes are checked explicitly below instead.
$PSNativeCommandUseErrorActionPreference = $false

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'wpr needs an elevated PowerShell. Re-run this as Administrator.'
}

$script:wpr = Join-Path $env:SystemRoot 'System32\wpr.exe'
if (-not (Test-Path $wpr)) { throw "wpr.exe not found at $wpr" }

$OutDir = (New-Item -ItemType Directory -Force -Path $OutDir).FullName
$staging = Join-Path $env:TEMP ('launchlab-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $staging | Out-Null

function Write-Utf8NoBom([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding($false)))
}

function Invoke-Wpr([string[]]$WprArgs) {
    # Windows PowerShell turns anything a native command writes to stderr into an ErrorRecord,
    # which is terminating under ErrorActionPreference=Stop. wpr writes there routinely
    # (-cancel with no session running, for one), so its streams are captured here and the
    # exit code is returned for the caller to check.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $script:wprOutput = (& $script:wpr @WprArgs 2>&1 | Out-String).Trim()
        return $LASTEXITCODE
    } finally { $ErrorActionPreference = $prev }
}

function Invoke-Workload([string]$Exe) {
    # Warm-up and measured runs go through this one path, so they are always the same
    # command line. Output goes through pipes, never files: redirecting to a temp file would
    # make the workload write to disk on the harness's behalf, and that write would land in
    # the workload's own disk attribution - the measurement tool contaminating its subject.
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    if ($AppArgs) { $psi.Arguments = $AppArgs }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = [Diagnostics.Process]::Start($psi)
    # Drain before waiting: a full pipe buffer would block the child and be measured as its cost.
    $p.StandardOutput.ReadToEnd() | Out-Null
    $p.StandardError.ReadToEnd() | Out-Null
    $p.WaitForExit()
    $sw.Stop()
    [pscustomobject]@{ Id = $p.Id; WallMs = $sw.Elapsed.TotalMilliseconds }
}

function Add-Utf8NoBom([string]$Path, [string]$Line) {
    [IO.File]::AppendAllText($Path, $Line + "`n", (New-Object Text.UTF8Encoding($false)))
}

function Get-PeMachine([string]$Path) {
    # Read the PE header directly. "Which architecture is this binary?" is the entire premise
    # of the emulation comparison, so it gets recorded rather than inferred from a folder name.
    try {
        $fs = [IO.File]::OpenRead($Path)
        try {
            $br = New-Object IO.BinaryReader($fs)
            $fs.Position = 0x3C
            $fs.Position = $br.ReadInt32() + 4
            switch ($br.ReadUInt16()) {
                0xAA64 { 'ARM64' }
                0x8664 { 'x64' }
                0x01C4 { 'ARM32' }
                0x014C { 'x86' }
                default { 'unknown' }
            }
        } finally { $fs.Dispose() }
    } catch { 'unknown' }
}

function Copy-AppDir([string]$ExePath, [string]$Destination) {
    # Copy the whole directory, not just the exe: most apps load siblings next to themselves.
    $src = Split-Path -Parent $ExePath
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Copy-Item -Path (Join-Path $src '*') -Destination $Destination -Recurse -Force
    Join-Path $Destination (Split-Path -Leaf $ExePath)
}

function Clear-XtaCache {
    try {
        Stop-Service XtaCache -Force -ErrorAction Stop
        Remove-Item 'C:\Windows\XtaCache\*' -Force -Recurse -ErrorAction SilentlyContinue
        Start-Service XtaCache -ErrorAction Stop
    } catch {
        Write-Warning "XtaCache reset failed ($_). Continuing; the fresh copy still gives a cold translation cache."
    }
}

# --- Build the config list ---------------------------------------------------------------
$modes = @($Mode)
if ($modes -contains 'coldxta' -and $modes.Count -gt 1) {
    # XtaCache is machine-wide. Clearing it between interleaved runs would strip the
    # translation cache out from under the warm arm too, so the comparison would be
    # measuring the clearing, not the configuration. Refuse rather than silently mislead.
    throw 'coldxta clears a machine-wide cache and cannot be interleaved with other modes. Run it as its own invocation (-Mode coldxta) and compare the two runs/csv with: launchlab compare'
}
$configs = @()
foreach ($entry in $Apps) {
    $label, $path = $entry -split '=', 2
    if (-not $path) { throw "bad -Apps entry '$entry', expected label=path" }
    if (-not (Test-Path $path)) { throw "app not found: $path" }
    foreach ($m in $modes) {
        $name = if ($Apps.Count -eq 1 -and $modes.Count -eq 1) { $label } else { "$label-$m" }
        $warmCopy = if ($m -ne 'coldfile') { Copy-AppDir $path (Join-Path $staging "fixed-$label") } else { $null }
        $configs += [pscustomobject]@{
            Name     = $name
            Index    = $configs.Count
            Source   = $path
            Mode     = $m
            WarmCopy = $warmCopy
        }
    }
}

# --- Pick a recording profile the machine will actually accept -----------------------------
$activeProfile = $WprProfile
$cpuSampling = $true
if (-not $NoTrace) {
    Invoke-Wpr @('-cancel') | Out-Null
    if ((Invoke-Wpr @('-start', $WprProfile, '-filemode')) -ne 0) {
        Invoke-Wpr @('-cancel') | Out-Null
        if ((Invoke-Wpr @('-start', $WprProfileFallback, '-filemode')) -ne 0) {
            throw "neither recording profile could be started. Last output:`n$script:wprOutput"
        }
        $activeProfile = $WprProfileFallback
        $cpuSampling = $false
        Write-Warning 'CPU sampling is not available on this machine (no PMU exposed to the guest?). Falling back to the no-sampling profile: cpu_ms and CPU-by-module will be absent.'
    }
    Invoke-Wpr @('-cancel') | Out-Null
}

# --- Environment disclosure ---------------------------------------------------------------
$os = Get-CimInstance Win32_OperatingSystem
$cs = Get-CimInstance Win32_ComputerSystem
$cpu = @(Get-CimInstance Win32_Processor)[0]
$ubr = try { (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').UBR } catch { '?' }
$defender = try { (Get-MpComputerStatus).RealTimeProtectionEnabled } catch { 'unknown' }
$power = try { (powercfg /getactivescheme) -join ' ' } catch { 'unknown' }
$media = try {
    $diskNo = (Get-Partition -DriveLetter C -ErrorAction Stop).DiskNumber
    (Get-PhysicalDisk -ErrorAction Stop | Where-Object { $_.DeviceId -eq "$diskNo" }).MediaType
} catch { 'unknown' }
$acPower = try {
    $bat = @(Get-CimInstance Win32_Battery -ErrorAction Stop)
    if ($bat.Count -eq 0) { 'no battery (AC)' } elseif ($bat[0].BatteryStatus -eq 2) { 'AC' } else { 'battery' }
} catch { 'unknown' }

$env_ = [ordered]@{
    generated_at    = (Get-Date).ToString('s')
    machine         = $env:COMPUTERNAME
    os              = "$($os.Caption) build $($os.BuildNumber).$ubr"
    architecture    = $env:PROCESSOR_ARCHITECTURE
    cpu             = $cpu.Name.Trim()
    logical_cpus    = $cs.NumberOfLogicalProcessors
    ram_gb          = [math]::Round($cs.TotalPhysicalMemory / 1GB, 1)
    machine_model   = "$($cs.Manufacturer) $($cs.Model)"
    virtual_machine = ($cs.Model -match 'Virtual|VMware|Hyper-V')
    hypervisor_present = $cs.HypervisorPresent
    system_disk_media = "$media"
    on_ac_power     = $acPower
    defender_realtime = "$defender"
    power_plan      = $power.Trim()
    wpr_profile     = $(if ($NoTrace) { 'none (-NoTrace)' } else { Split-Path -Leaf $activeProfile })
    cpu_sampling    = $(if ($NoTrace) { $false } else { $cpuSampling })
    app_args        = $AppArgs
    runs_per_config = $N
    settle_seconds  = $SettleSeconds
    interleaved     = $true
    alternate_tracing = [bool]$AlternateTracing
    configs         = ($configs | ForEach-Object { "$($_.Name) <- $($_.Source) [$($_.Mode), $(Get-PeMachine $_.Source)]" }) -join '; '
}
Write-Utf8NoBom (Join-Path $OutDir 'env.json') ($env_ | ConvertTo-Json -Depth 4)

$total = $N * $configs.Count
Write-Host "LaunchLab: $($configs.Count) configs x $N runs = $total traces into $OutDir" -ForegroundColor Cyan
Write-Host ("estimated wall clock: ~{0:N0} min" -f ($total * ($SettleSeconds + 6) / 60)) -ForegroundColor DarkGray

# One untraced warm-up per app so the very first measured run is not paying for
# OS-wide first-touch costs that have nothing to do with the config under test.
if ($modes -notcontains 'coldxta') {
    foreach ($c in $configs | Group-Object Source | ForEach-Object { $_.Group[0] }) {
        1..3 | ForEach-Object { Invoke-Workload $c.Source | Out-Null }
    }
    # XtaCache writes its .JC translation files asynchronously; give it a moment to settle
    # so the warm arm really is warm.
    Start-Sleep -Seconds 5
}

$overheadCsv = Join-Path $OutDir 'overhead.csv'
if (-not (Test-Path $overheadCsv)) { Add-Utf8NoBom $overheadCsv 'config,run,traced,wall_ms' }

Invoke-Wpr @('-cancel') | Out-Null   # clear any session left behind by an interrupted run

$done = 0
for ($i = 1; $i -le $N; $i++) {
    foreach ($c in $configs) {
        $done++
        $tag = '{0}-{1}-{2:d3}' -f ('c' + $c.Index), $c.Name, $i
        $etl = Join-Path $OutDir "$tag.etl"
        # Alternate on the run index, not the running total: alternating on the total would
        # hand every traced run to the first config and every untraced run to the second.
        $traceThis = (-not $NoTrace) -and ((-not $AlternateTracing) -or ($i % 2 -eq 1))

        switch ($c.Mode) {
            'coldfile' { $exe = Copy-AppDir $c.Source (Join-Path $staging "cold-$tag") }
            'coldxta'  { $exe = $c.WarmCopy; Clear-XtaCache }
            default    { $exe = $c.WarmCopy }
        }

        if ($traceThis) {
            $rc = Invoke-Wpr @('-start', $activeProfile, '-filemode')
            if ($rc -ne 0) { throw "wpr -start failed with $rc`n$script:wprOutput" }
        }

        $r = Invoke-Workload $exe

        if ($traceThis) {
            $rc = Invoke-Wpr @('-stop', $etl)
            if ($rc -ne 0) { throw "wpr -stop failed with $rc`n$script:wprOutput" }
        }

        # Wall clock of every run, traced or not, so the report can price the tracing itself.
        Add-Utf8NoBom $overheadCsv ('{0},{1},{2},{3:F3}' -f $c.Name, $i, $(if ($traceThis) { 1 } else { 0 }), $r.WallMs)

        if ($traceThis) { Write-Utf8NoBom "$etl.json" ([ordered]@{
            config   = $c.Name
            run      = $i
            pid      = $r.Id
            exe      = $exe
            mode     = $c.Mode
            wall_ms  = [math]::Round($r.WallMs, 3)
            started  = (Get-Date).ToString('s')
        } | ConvertTo-Json -Compress) }

        Write-Host ("[{0,3}/{1}] {2,-24} pid {3,-6} wall {4,7:N1} ms" -f $done, $total, $c.Name, $r.Id, $r.WallMs)
        Start-Sleep -Seconds $SettleSeconds
    }
}

Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "done. Next: dotnet run --project src\LaunchLab -- analyze $OutDir" -ForegroundColor Green
