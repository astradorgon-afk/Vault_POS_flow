#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Fails if merged line coverage has fallen below the floors recorded here.

.DESCRIPTION
    `dotnet test --collect:"XPlat Code Coverage"` writes one Cobertura report per
    test project, each one measuring every assembly that project happened to
    load. No single report is the answer: Pos.Domain looks thin in the API report
    and thick in its own. This script unions them, so a line counts as covered
    when any suite covered it.

    Two details make that union harder than it looks:

      * Each report's file names are relative to its own <source> root, which
        coverlet derives from the common prefix of the files in that report. The
        same file is 'Pos.Domain/Sales/Sale.cs' in one and
        'src/Pos.Domain/Sales/Sale.cs' in the next, so the roots are joined back
        on before anything is compared.
      * A class's lines appear twice, once under <methods> and once under the
        class itself, so lines are keyed and deduplicated rather than summed.

    The floors are a ratchet, not an aspiration: each sits a couple of points
    under what the suite actually covers today, so ordinary work never trips it
    and a real regression does. Raise them when coverage rises. Lowering one is
    a decision worth writing down in the commit message.

.PARAMETER ResultsDirectory
    Directory searched recursively for coverage.cobertura.xml files.

.PARAMETER MinimumTotal
    Overrides the total line-coverage floor, for a one-off local run.

.PARAMETER MinimumBranchTotal
    Overrides the total branch-coverage floor.
#>

[CmdletBinding()]
param(
    [string] $ResultsDirectory,
    [double] $MinimumTotal = 78,
    [double] $MinimumBranchTotal = 58
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is not populated while parameter defaults are bound under every
# host, so the repository root is resolved here instead.
$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = (Resolve-Path (Join-Path $scriptDirectory '..')).Path

if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $ResultsDirectory = Join-Path $root 'artifacts/test-results'
}

# Per-assembly floors. Pos.Web and Pos.SharedUI carry no tests at all, so they
# are listed at zero rather than left out: they still count towards the total,
# which is what keeps an untested UI visible instead of quietly averaged away.
$floors = [ordered]@{
    'Pos.Api'            = 84
    'Pos.Application'    = 84
    'Pos.Domain'         = 81
    'Pos.Infrastructure' = 82
    'Pos.Shared'         = 90
    'Pos.SharedUI'       = 0
    'Pos.Web'            = 0
}

if (-not (Test-Path $ResultsDirectory)) {
    Write-Error "No coverage results under '$ResultsDirectory'. Run dotnet test with --collect:`"XPlat Code Coverage`" first."
}

$reports = @(Get-ChildItem -Path $ResultsDirectory -Filter 'coverage.cobertura.xml' -Recurse -File)
if ($reports.Count -eq 0) {
    Write-Error "No coverage.cobertura.xml under '$ResultsDirectory'."
}

# key -> hits, and key -> [covered, total] for the conditions on a branch line.
$lineHits = @{}
$branches = @{}

foreach ($report in $reports) {
    $xml = [xml] (Get-Content -LiteralPath $report.FullName -Raw)

    $sources = @($xml.SelectNodes('/coverage/sources/source') | ForEach-Object { $_.InnerText })

    foreach ($class in $xml.SelectNodes('//packages/package/classes/class')) {
        $assembly = $class.ParentNode.ParentNode.GetAttribute('name')
        $file = $class.GetAttribute('filename')

        # Resolve the report-relative name against whichever root produces a
        # path that exists, so the same file from two reports lands on one key.
        if (-not [System.IO.Path]::IsPathRooted($file)) {
            $resolved = $null
            foreach ($source in $sources) {
                $candidate = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($source, $file))
                if (Test-Path -LiteralPath $candidate) { $resolved = $candidate; break }
            }
            if (-not $resolved -and $sources.Count -gt 0) {
                $resolved = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($sources[0], $file))
            }
            if ($resolved) { $file = $resolved }
        }

        foreach ($line in $class.SelectNodes('.//line')) {
            $key = "$assembly|$file|$($line.GetAttribute('number'))"

            $hits = [int] $line.GetAttribute('hits')
            if (-not $lineHits.ContainsKey($key) -or $lineHits[$key] -lt $hits) {
                $lineHits[$key] = $hits
            }

            $condition = $line.GetAttribute('condition-coverage')
            if ($line.GetAttribute('branch') -eq 'True' -and $condition -match '\((\d+)/(\d+)\)') {
                $covered = [int] $Matches[1]
                $total = [int] $Matches[2]
                $seen = $branches[$key]
                if ($null -eq $seen) {
                    $branches[$key] = @($covered, $total)
                }
                else {
                    $branches[$key] = @([Math]::Max($seen[0], $covered), [Math]::Max($seen[1], $total))
                }
            }
        }
    }
}

$measured = @{}
foreach ($entry in $lineHits.GetEnumerator()) {
    $assembly = $entry.Key.Split('|')[0]
    if (-not $measured.ContainsKey($assembly)) { $measured[$assembly] = @{ Covered = 0; Total = 0 } }
    $measured[$assembly].Total++
    if ($entry.Value -gt 0) { $measured[$assembly].Covered++ }
}

$branchCovered = 0
$branchTotal = 0
foreach ($value in $branches.Values) {
    $branchCovered += $value[0]
    $branchTotal += $value[1]
}

$failures = [System.Collections.Generic.List[string]]::new()
$totalCovered = 0
$totalLines = 0

Write-Host ''
Write-Host ('{0,-22}{1,9}{2,9}{3,9}{4,9}' -f 'assembly', 'lines', 'covered', 'percent', 'floor')
Write-Host ('-' * 58)

foreach ($assembly in ($measured.Keys | Sort-Object)) {
    $covered = $measured[$assembly].Covered
    $total = $measured[$assembly].Total
    $totalCovered += $covered
    $totalLines += $total

    $percent = if ($total -gt 0) { 100 * $covered / $total } else { 100 }

    $floor = $null
    if ($floors.Contains($assembly)) { $floor = [double] $floors[$assembly] }

    $shown = if ($null -eq $floor) { '-' } else { '{0:N1}%' -f $floor }
    Write-Host ('{0,-22}{1,9}{2,9}{3,8:N1}%{4,9}' -f $assembly, $total, $covered, $percent, $shown)

    if ($null -ne $floor -and $percent -lt $floor) {
        $failures.Add(('{0} line coverage is {1:N1}%, below its {2:N1}% floor.' -f $assembly, $percent, $floor))
    }
}

# An assembly that disappears from the reports reads as a pass on every floor it
# had, which is the quietest way for a gate like this to stop working.
foreach ($assembly in $floors.Keys) {
    if (-not $measured.ContainsKey($assembly)) {
        $failures.Add("$assembly has a coverage floor but no coverage was reported for it.")
    }
}

$totalPercent = if ($totalLines -gt 0) { 100 * $totalCovered / $totalLines } else { 0 }
$branchPercent = if ($branchTotal -gt 0) { 100 * $branchCovered / $branchTotal } else { 0 }

Write-Host ('-' * 58)
Write-Host ('{0,-22}{1,9}{2,9}{3,8:N1}%{4,8:N1}%' -f 'TOTAL', $totalLines, $totalCovered, $totalPercent, $MinimumTotal)
Write-Host ('{0,-22}{1,9}{2,9}{3,8:N1}%{4,8:N1}%' -f 'branches', $branchTotal, $branchCovered, $branchPercent, $MinimumBranchTotal)
Write-Host ''
Write-Host ("Merged from $($reports.Count) report(s) under $ResultsDirectory.")

if ($totalPercent -lt $MinimumTotal) {
    $failures.Add(('Total line coverage is {0:N1}%, below the {1:N1}% floor.' -f $totalPercent, $MinimumTotal))
}
if ($branchPercent -lt $MinimumBranchTotal) {
    $failures.Add(('Total branch coverage is {0:N1}%, below the {1:N1}% floor.' -f $branchPercent, $MinimumBranchTotal))
}

if ($failures.Count -gt 0) {
    Write-Host ''
    foreach ($failure in $failures) { Write-Host "  $failure" }
    Write-Host ''
    Write-Error 'Coverage is below the floors in scripts/check-coverage.ps1.'
}

Write-Host 'Coverage is at or above every floor.'
