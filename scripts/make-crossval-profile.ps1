<#
.SYNOPSIS
    Derives a minimal WPA profile for cross-validating LaunchLab against WPA itself.

.DESCRIPTION
    wpaexporter needs a .wpaProfile to know which tables and columns to export. Rather
    than hand-authoring one (the format is WPA's serialized view state, not a documented
    schema), this trims the AppLaunch profile that ships in the ADK catalog down to the
    two tables LaunchLab's metrics come from:

        CPU Usage (Precise)   -> Ready, Waits, on-CPU time
        Disk Usage            -> service time, bytes, IO count

    and un-hides the two columns AppLaunch leaves off: "New Switch-In Time" and
    "CPU Usage (in view)". Those are WPA's name for what LaunchLab calls cpu_on_ms.

    Everything else is removed so the export produces exactly two CSVs with unique
    column names -- AppLaunch leaves Ready and Waits in twice (Sum and Max), which
    makes the CSV unparseable by header.
#>
[CmdletBinding()]
param(
    [string] $WptDir,
    [string] $OutFile = (Join-Path $PSScriptRoot 'launchlab-crossval.wpaProfile')
)

$ErrorActionPreference = 'Stop'

if (-not $WptDir) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\Windows Performance Toolkit"
        "$env:ProgramFiles\Windows Kits\10\Windows Performance Toolkit"
    )
    $WptDir = $candidates | Where-Object { Test-Path (Join-Path $_ 'wpaexporter.exe') } | Select-Object -First 1
}
if (-not $WptDir) {
    throw "Windows Performance Toolkit not found. Install it with: adksetup.exe /features OptionId.WindowsPerformanceToolkit /quiet /ceip off"
}

$source = Join-Path $WptDir 'Catalog\AppLaunch.wpaProfile'
if (-not (Test-Path $source)) { throw "Not found: $source" }

# The two tables we need, by WPA graph GUID.
$keep = @{
    'c58f5fea-0319-4046-932d-e695ebe20b47' = 'CPU Usage (Precise)'
    '12c5387c-58ca-45d4-a5c6-450475c30e06' = 'Disk Usage'
}

# Columns to show in CPU Usage (Precise). Ready and Waits appear twice in the shipped
# preset -- once aggregated Sum, once Max -- so they are matched on GUID *and*
# AggregationMode, and the Max copies stay hidden.
$show = @{
    'b065487c-5e32-4f1f-a2cd-581e086ce29e' = $null    # New Process (key)
    'd227f58f-ec9b-4a52-8fe5-e082771c55c6' = 'Count'  # context switches in
    '906ea81e-ab68-4dfd-9b9f-3adafab60f83' = 'Sum'    # Ready
    '6d598aa8-2ec4-46cd-b71a-88a239dfacf7' = 'Sum'    # Waits
    '03a1d898-7231-4cc5-9712-4bfbf53908c7' = 'Sum'    # New Switch-In Time
    'e008ed7a-15b0-40ab-854b-b5f6392f298b' = 'Sum'    # CPU Usage (in view)
}

[xml] $xml = Get-Content -LiteralPath $source -Raw

function Remove-Nodes($nodes) {
    foreach ($n in @($nodes)) { [void] $n.ParentNode.RemoveChild($n) }
}

# Drop the regions file reference: it is what makes wpaexporter emit "Regions of
# Interest" errors and exit non-zero on traces that have no such regions.
Remove-Nodes $xml.GetElementsByTagName('FileReference')

# Drop every graph and schema except the tables above. AppLaunch lists the same graph
# several times, once per preset it wants exported; keeping one entry per table means one
# CSV per table.
$seen = @{}
Remove-Nodes ($xml.GetElementsByTagName('Graph') | Where-Object {
    $g = $_.Guid.ToLowerInvariant()
    if (-not $keep.ContainsKey($g)) { return $true }
    if ($seen.ContainsKey($g)) { return $true }
    $seen[$g] = $true
    return $false
})
Remove-Nodes ($xml.GetElementsByTagName('GraphSchema') | Where-Object { -not $keep.ContainsKey($_.Guid.ToLowerInvariant()) })

# Set column visibility on the CPU table. Positions are left alone -- the preset's
# KeyColumnCount / FrozenColumnCount attributes are positional, so hiding is safe
# where removing would not be.
$cpuSchema = $xml.GetElementsByTagName('GraphSchema') |
    Where-Object { $_.Guid.ToLowerInvariant() -eq 'c58f5fea-0319-4046-932d-e695ebe20b47' }
if (-not $cpuSchema) { throw 'CPU Usage (Precise) schema not found in the source profile' }

$shown = 0
foreach ($col in $cpuSchema.GetElementsByTagName('Column')) {
    $guid = $col.Guid.ToLowerInvariant()
    $want = $show.ContainsKey($guid) -and
            ($null -eq $show[$guid] -or $col.AggregationMode -eq $show[$guid])
    if ($want) {
        $col.SetAttribute('IsVisible', 'true')
        $shown++
    } elseif ($col.HasAttribute('IsVisible')) {
        $col.SetAttribute('IsVisible', 'false')
    }
}

# WPA falls back to a table's *built-in* preset unless the graph names one explicitly,
# so the edited presets are renamed and referenced by name. Without this the export
# silently ignores the column visibility above and emits WPA's stock column set.
# Open each table as a table, not as a graph with a table under it. wpaexporter does not
# care, but the same profile is what the WPA GUI opens for a visual check, and the numbers
# being compared live in the table.
foreach ($g in $xml.GetElementsByTagName('Graph')) {
    if ($keep.ContainsKey($g.Guid.ToLowerInvariant())) { $g.SetAttribute('LayoutStyle', 'Table') }
}

$presetName = 'LaunchLab'
foreach ($schema in $xml.GetElementsByTagName('GraphSchema')) {
    $first = @($schema.GetElementsByTagName('Preset')) | Select-Object -First 1
    if (-not $first) { continue }
    $first.SetAttribute('Name', $presetName)
    foreach ($g in $xml.GetElementsByTagName('Graph')) {
        if ($g.Guid.ToLowerInvariant() -eq $schema.Guid.ToLowerInvariant()) {
            $g.SetAttribute('PresetName', $presetName)
        }
    }
}

$xml.Save($OutFile)

"Source : $source"
"Output : $OutFile"
"Tables : $(($keep.Values | Sort-Object) -join ', ')"
"Columns shown in CPU Usage (Precise): $shown"
