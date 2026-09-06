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

    [ValidateSet('warm', 'cold', 'both')]
    [string]$Mode = 'both',

    [int]$N = 20,

    [string]$OutDir = (Join-Path $PSScriptRoot '..\results'),

    # Arguments passed to the app. The default (none) makes 7z print its usage and exit,
    # which keeps the measurement to process startup and nothing else.
    [string]$AppArgs = '',

    [int]$SettleSeconds = 3,

    # Also stop the XtaCache service and clear C:\Windows\XtaCache before each cold run.
    # Only meaningful on Windows on Arm, and much slower than the fresh-copy trick.
    [switch]$HardCold,

    # Run the same scenario without starting WPR, to measure what the tracing itself costs.
    # Produces no traces; appends wall-clock times to overhead.csv for the report to compare.
    [switch]$NoTrace
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

$wpr = Join-Path $env:SystemRoot 'System32\wpr.exe'
if (-not (Test-Path $wpr)) { throw "wpr.exe not found at $wpr" }

$OutDir = (New-Item -ItemType Directory -Force -Path $OutDir).FullName
$staging = Join-Path $env:TEMP ('launchlab-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $staging | Out-Null

function Write-Utf8NoBom([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding($false)))
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
$modes = if ($Mode -eq 'both') { @('warm', 'cold') } else { @($Mode) }
$configs = @()
foreach ($entry in $Apps) {
    $label, $path = $entry -split '=', 2
    if (-not $path) { throw "bad -Apps entry '$entry', expected label=path" }
    if (-not (Test-Path $path)) { throw "app not found: $path" }
    foreach ($m in $modes) {
        $name = if ($Apps.Count -eq 1 -and $modes.Count -eq 1) { $label } else { "$label-$m" }
        $warmCopy = if ($m -eq 'warm') { Copy-AppDir $path (Join-Path $staging "warm-$label") } else { $null }
        $configs += [pscustomobject]@{
            Name     = $name
            Index    = $configs.Count
            Source   = $path
            Mode     = $m
            WarmCopy = $warmCopy
        }
    }
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
    wpr_profiles    = $(if ($NoTrace) { 'none (-NoTrace)' } else { 'GeneralProfile + DiskIO' })
    app_args        = $AppArgs
    runs_per_config = $N
    settle_seconds  = $SettleSeconds
    interleaved     = $true
    configs         = ($configs | ForEach-Object { "$($_.Name) <- $($_.Source) [$($_.Mode), $(Get-PeMachine $_.Source)]" }) -join '; '
}
Write-Utf8NoBom (Join-Path $OutDir 'env.json') ($env_ | ConvertTo-Json -Depth 4)

$total = $N * $configs.Count
Write-Host "LaunchLab: $($configs.Count) configs x $N runs = $total traces into $OutDir" -ForegroundColor Cyan
Write-Host ("estimated wall clock: ~{0:N0} min" -f ($total * ($SettleSeconds + 6) / 60)) -ForegroundColor DarkGray

# One untraced warm-up per app so the very first measured run is not paying for
# OS-wide first-touch costs that have nothing to do with the config under test.
foreach ($c in $configs | Group-Object Source | ForEach-Object { $_.Group[0] }) {
    if ($AppArgs) { & $c.Source $AppArgs *> $null } else { & $c.Source *> $null }
}

$overheadCsv = Join-Path $OutDir 'overhead.csv'
if (-not (Test-Path $overheadCsv)) { Add-Utf8NoBom $overheadCsv 'config,run,traced,wall_ms' }

& $wpr -cancel 2>&1 | Out-Null   # clear any session left behind by an interrupted run

$done = 0
for ($i = 1; $i -le $N; $i++) {
    foreach ($c in $configs) {
        $done++
        $tag = '{0}-{1}-{2:d3}' -f ('c' + $c.Index), $c.Name, $i
        $etl = Join-Path $OutDir "$tag.etl"

        if ($c.Mode -eq 'cold') {
            $exe = Copy-AppDir $c.Source (Join-Path $staging "cold-$tag")
            if ($HardCold) { Clear-XtaCache }
        } else {
            $exe = $c.WarmCopy
        }

        if (-not $NoTrace) {
            & $wpr -start GeneralProfile -start DiskIO -filemode
            if ($LASTEXITCODE -ne 0) { throw "wpr -start failed with $LASTEXITCODE" }
        }

        $stdout = [IO.Path]::GetTempFileName()
        $stderr = [IO.Path]::GetTempFileName()
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $p = if ($AppArgs) {
            Start-Process -FilePath $exe -ArgumentList $AppArgs -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        } else {
            Start-Process -FilePath $exe -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        }
        $p.WaitForExit()
        $sw.Stop()
        Remove-Item $stdout, $stderr -Force -ErrorAction SilentlyContinue

        if (-not $NoTrace) {
            & $wpr -stop $etl
            if ($LASTEXITCODE -ne 0) { throw "wpr -stop failed with $LASTEXITCODE" }
        }

        # Wall clock of every run, traced or not, so the report can price the tracing itself.
        Add-Utf8NoBom $overheadCsv ('{0},{1},{2},{3:F3}' -f $c.Name, $i, $(if ($NoTrace) { 0 } else { 1 }), $sw.Elapsed.TotalMilliseconds)

        if (-not $NoTrace) { Write-Utf8NoBom "$etl.json" ([ordered]@{
            config   = $c.Name
            run      = $i
            pid      = $p.Id
            exe      = $exe
            mode     = $c.Mode
            wall_ms  = [math]::Round($sw.Elapsed.TotalMilliseconds, 3)
            started  = (Get-Date).ToString('s')
        } | ConvertTo-Json -Compress) }

        Write-Host ("[{0,3}/{1}] {2,-24} pid {3,-6} wall {4,7:N1} ms" -f $done, $total, $c.Name, $p.Id, $sw.Elapsed.TotalMilliseconds)
        Start-Sleep -Seconds $SettleSeconds
    }
}

Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "done. Next: dotnet run --project src\LaunchLab -- analyze $OutDir" -ForegroundColor Green
