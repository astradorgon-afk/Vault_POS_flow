[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string] $Path = "artifacts/test-results",

    [Parameter(Mandatory = $false)]
    [double] $MinimumLineRate = 0.60
)

$coverageFiles = Get-ChildItem -LiteralPath $Path -Filter "coverage.cobertura.xml" -File -Recurse -ErrorAction SilentlyContinue
if ($null -eq $coverageFiles -or $coverageFiles.Count -eq 0) {
    throw "No Cobertura coverage files were found below '$Path'."
}

$lineCoverage = @{}
$coverageFiles | ForEach-Object {
    Write-Host ("Reading {0}" -f $_.FullName)
}

foreach ($file in $coverageFiles) {
    [xml] $coverage = Get-Content -LiteralPath $file.FullName -Raw

    foreach ($class in @($coverage.coverage.packages.package.classes.class)) {
        $sourcePath = [string] $class.filename

        # Test projects emit dependency coverage for every referenced project.
        # Merge only VaultFlow's first-party server/shared source lines and omit
        # MAUI/client-generated native graphs, which have no test assemblies.
        if ($sourcePath -notmatch '(^|[/\\])src[/\\]Pos\.(Api|Application|Domain|Infrastructure|Shared|SharedUI|Web)[/\\]') {
            continue
        }

        foreach ($line in @($class.lines.line)) {
            $key = "{0}|{1}" -f $sourcePath.ToLowerInvariant(), [string] $line.number
            $hit = ([int64] $line.hits) -gt 0
            if (-not $lineCoverage.ContainsKey($key)) {
                $lineCoverage[$key] = $hit
            }
            elseif ($hit) {
                $lineCoverage[$key] = $true
            }
        }
    }
}

$validLines = $lineCoverage.Count
$coveredLines = @($lineCoverage.Values | Where-Object { $_ }).Count
if ($validLines -le 0) {
    throw "No first-party source lines were found in the Cobertura files below '$Path'."
}

$aggregateRate = $coveredLines / $validLines
Write-Host ("Merged first-party line coverage: {0:P2} ({1}/{2}); required: {3:P2}" -f $aggregateRate, $coveredLines, $validLines, $MinimumLineRate)

if ($aggregateRate -lt $MinimumLineRate) {
    throw "Coverage gate failed: aggregate line coverage is below the required threshold."
}
