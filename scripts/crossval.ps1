<#
.SYNOPSIS
    Cross-validates LaunchLab's numbers against Microsoft's own tools on the same traces.

.DESCRIPTION
    LaunchLab reads ETL files with the TraceProcessing SDK. This re-reads the exact same
    files with two independent Microsoft implementations and diffs the results:

        wpaexporter.exe  -- WPA's analysis engine, driven from the command line.
                            Supplies Ready, Waits, disk service time, bytes and IO count.
        xperf.exe -a process
                         -- Supplies process start and end timestamps, which is what
                            LaunchLab reports as startup_ms.

    Nothing here re-runs the workload. All three tools read identical bytes, so a
    disagreement is an analysis bug, not measurement noise.

    Not covered: cpu_on_ms. WPA's equivalent column ("CPU Usage (in view)") exists in the
    CPU Usage (Precise) table but is hidden in the built-in preset, and wpaexporter ignores
    the column visibility set in a .wpaProfile -- it uses a profile only to choose which
    tables to export, then falls back to each table's stock preset. The other table that
    does expose CPU time in milliseconds, CPU Usage (Attributed), resolves stacks and takes
    minutes per trace. So cpu_on_ms is left to the WPA GUI to confirm by hand.

.EXAMPLE
    .\scripts\crossval.ps1 -ResultsDir .\results\warm2
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ResultsDir,
    [string] $WptDir,
    [string] $ProfilePath,
    [int]    $Limit = 0
)

$ErrorActionPreference = 'Stop'

if (-not $WptDir) {
    $WptDir = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\Windows Performance Toolkit"
        "$env:ProgramFiles\Windows Kits\10\Windows Performance Toolkit"
    ) | Where-Object { Test-Path (Join-Path $_ 'wpaexporter.exe') } | Select-Object -First 1
}
if (-not $WptDir) { throw 'Windows Performance Toolkit not found. See make-crossval-profile.ps1.' }

if (-not $ProfilePath) { $ProfilePath = Join-Path $PSScriptRoot 'launchlab-crossval.wpaProfile' }
if (-not (Test-Path $ProfilePath)) {
    & (Join-Path $PSScriptRoot 'make-crossval-profile.ps1') -WptDir $WptDir -OutFile $ProfilePath | Out-Null
}

$ResultsDir = (Resolve-Path $ResultsDir).Path
$runsCsv = Join-Path $ResultsDir 'runs.csv'
if (-not (Test-Path $runsCsv)) { throw "No runs.csv in $ResultsDir" }

$wpaexporter = Join-Path $WptDir 'wpaexporter.exe'
$xperf       = Join-Path $WptDir 'xperf.exe'

# WPA writes thousands separators into its CSVs: "2,876.373".
function ConvertTo-Number([string] $s) {
    if ([string]::IsNullOrWhiteSpace($s)) { return 0.0 }
    $v = 0.0
    if ([double]::TryParse(($s -replace ',', ''), [Globalization.NumberStyles]::Float,
                           [Globalization.CultureInfo]::InvariantCulture, [ref] $v)) { return $v }
    return 0.0
}

# WPA's stock CPU preset emits "Ready" and "Waits" twice each -- once summed, once as a
# maximum -- and Import-Csv refuses a duplicate header outright. Suffix the repeats so the
# first (summed) copy keeps its name and can still be found by prefix.
function Import-WpaCsv([string] $Path) {
    $lines = Get-Content -LiteralPath $Path
    if ($lines.Count -lt 2) { return @() }
    $seen = @{}
    $header = ($lines[0] -split ',' | ForEach-Object {
        if ($seen.ContainsKey($_)) { $seen[$_]++; "$($_)_$($seen[$_])" } else { $seen[$_] = 1; $_ }
    }) -join ','
    $tmp = [IO.Path]::GetTempFileName()
    Set-Content -LiteralPath $tmp -Value (@($header) + $lines[1..($lines.Count - 1)])
    try { return @(Import-Csv $tmp) } finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
}

# Column headers carry a micro sign whose encoding survives neither PowerShell 5.1's default
# reader nor the trip through ssh, so columns are matched on an ASCII prefix. The prefixes
# have to be specific: "Ready*" would match "Readying Process", which sits earlier in the row.
# xperf right-aligns the pid inside the parentheses ("7z.exe ( 512)") while WPA does not
# ("7z.exe (9076)"). A -like "*($target)*" match silently misses every pid under four
# digits, which reads as the tool disagreeing rather than as a bug here.
function Test-Pid([string] $field, [int] $target) {
    return $field -match ('\(\s*' + $target + '\s*\)')
}

function Get-Col($row, [string] $prefix) {
    $p = $row.PSObject.Properties | Where-Object { $_.Name -like "$prefix*" } | Select-Object -First 1
    if ($p) { return ConvertTo-Number $p.Value }
    return 0.0
}

$scratch = Join-Path $env:TEMP ("launchlab-xval-" + [Guid]::NewGuid().ToString('n').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $scratch | Out-Null

$rows = @(Import-Csv $runsCsv)
if ($Limit -gt 0) { $rows = @($rows | Select-Object -First $Limit) }

$out = New-Object Collections.Generic.List[object]
$i = 0
foreach ($r in $rows) {
    $i++
    $etl = Join-Path $ResultsDir $r.etl
    if (-not (Test-Path $etl)) { Write-Warning "missing trace, skipping: $($r.etl)"; continue }
    Write-Host ("[{0}/{1}] {2}" -f $i, $rows.Count, $r.etl)
    $target = [int] $r.pid

    $exportDir = Join-Path $scratch ([IO.Path]::GetFileNameWithoutExtension($etl))
    New-Item -ItemType Directory -Force -Path $exportDir | Out-Null

    # wpaexporter writes progress and non-fatal table warnings to stderr, and PowerShell 5.1
    # turns native stderr into a terminating error under ErrorActionPreference='Stop'.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & $wpaexporter -i $etl -profile $ProfilePath -outputfolder $exportDir 2>&1 | Out-Null
    $procCsv = Join-Path $exportDir 'process.csv'
    & $xperf -i $etl -o $procCsv -a process 2>&1 | Out-Null
    $ErrorActionPreference = $prev

    # --- WPA: CPU Usage (Precise), aggregated to one row per process --------------------
    $wpaReady = 0.0; $wpaWait = 0.0; $wpaCsw = 0
    $cpuFile = Get-ChildItem $exportDir -Filter 'CPU_Usage_(Precise)*.csv' -EA SilentlyContinue |
               Select-Object -First 1
    if ($cpuFile) {
        $row = Import-WpaCsv $cpuFile.FullName |
               Where-Object { Test-Pid $_.'New Process' $target } | Select-Object -First 1
        if ($row) {
            $wpaReady = (Get-Col $row 'Ready (') / 1000.0
            $wpaWait  = (Get-Col $row 'Waits (') / 1000.0
            $wpaCsw   = [int] (Get-Col $row 'Count')
        }
    }

    # --- WPA: Disk Usage. One row per (process, IO type, path), not per IO: WPA groups
    #     them, summing Size and Disk Service Time. So the row count is not an IO count
    #     and is reported, not compared.
    $wpaDiskMs = 0.0; $wpaDiskBytes = 0.0; $wpaDiskRows = 0
    $diskFile = Get-ChildItem $exportDir -Filter 'Disk_Usage*.csv' -EA SilentlyContinue |
                Select-Object -First 1
    if ($diskFile) {
        foreach ($d in (Import-WpaCsv $diskFile.FullName | Where-Object { Test-Pid $_.Process $target })) {
            $wpaDiskMs    += (Get-Col $d 'Disk Service Time') / 1000.0
            $wpaDiskBytes += (Get-Col $d 'Size (')
            $wpaDiskRows++
        }
    }

    # --- xperf: process lifetime, in microseconds ---------------------------------------
    $xperfStartup = 0.0
    if (Test-Path $procCsv) {
        foreach ($line in (Get-Content $procCsv)) {
            $f = $line -split ',' | ForEach-Object { $_.Trim() }
            if ($f.Count -lt 5 -or $f[2] -ne 'Process') { continue }
            if (-not (Test-Pid $f[4] $target)) { continue }
            if ($f[0] -eq 'MIN' -or $f[1] -eq 'MAX') { continue }
            $xperfStartup = ((ConvertTo-Number $f[1]) - (ConvertTo-Number $f[0])) / 1000.0
            break
        }
    }

    Remove-Item $exportDir -Recurse -Force -ErrorAction SilentlyContinue

    $out.Add([pscustomobject] [ordered] @{
        config              = $r.config
        run                 = $r.run
        etl                 = $r.etl
        pid                 = $target
        ll_startup_ms       = [double] $r.startup_ms
        xperf_startup_ms    = [math]::Round($xperfStartup, 3)
        ll_ready_ms         = [double] $r.ready_ms
        wpa_ready_ms        = [math]::Round($wpaReady, 3)
        ll_wait_ms          = [double] $r.wait_ms
        wpa_wait_ms         = [math]::Round($wpaWait, 3)
        ll_disk_service_ms  = [double] $r.disk_service_ms
        wpa_disk_service_ms = [math]::Round($wpaDiskMs, 3)
        ll_disk_ios         = [int] $r.disk_ios
        wpa_disk_groups     = $wpaDiskRows
        ll_disk_mb          = [double] $r.disk_mb
        wpa_disk_mb         = [math]::Round($wpaDiskBytes / 1MB, 3)
        wpa_context_switches = $wpaCsw
    })
}

$dest = Join-Path $ResultsDir 'crossval.csv'
$csv = ($out | ConvertTo-Csv -NoTypeInformation) -join "`n"
[IO.File]::WriteAllText($dest, $csv + "`n", (New-Object Text.UTF8Encoding $false))
Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue

''
"Wrote $dest ($($out.Count) runs)"
''
'{0,-22} {1,6} {2,14} {3,12}' -f 'metric', 'runs', 'max |diff|', 'max rel'
'{0,-22} {1,6} {2,14} {3,12}' -f '----------------------', '------', '--------------', '------------'
foreach ($pair in @(
    @('startup_ms',      'll_startup_ms',      'xperf_startup_ms'),
    @('ready_ms',        'll_ready_ms',        'wpa_ready_ms'),
    @('wait_ms',         'll_wait_ms',         'wpa_wait_ms'),
    @('disk_service_ms', 'll_disk_service_ms', 'wpa_disk_service_ms'),
    @('disk_mb',         'll_disk_mb',         'wpa_disk_mb')
)) {
    $name, $a, $b = $pair
    $maxAbs = 0.0; $maxRel = 0.0
    foreach ($o in $out) {
        $x = [double] $o.$a; $y = [double] $o.$b
        $d = [math]::Abs($x - $y)
        if ($d -gt $maxAbs) { $maxAbs = $d }
        $scale = [math]::Max([math]::Abs($x), [math]::Abs($y))
        if ($scale -gt 0 -and ($d / $scale) -gt $maxRel) { $maxRel = $d / $scale }
    }
    '{0,-22} {1,6} {2,14:F4} {3,11:P3}' -f $name, $out.Count, $maxAbs, $maxRel
}
