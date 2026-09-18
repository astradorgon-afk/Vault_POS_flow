[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string] $OutputDirectory = "backups"
)

$resolvedDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $resolvedDirectory -Force | Out-Null
$stamp = Get-Date -AsUTC -Format "yyyyMMdd-HHmmss"
$outputPath = Join-Path $resolvedDirectory "vaultflow-$stamp.dump"

Write-Host "Creating PostgreSQL custom-format backup at $outputPath"
& docker compose exec -T postgres pg_dump -U pos_migrator -d vaultflow --format=custom --no-owner --no-acl > $outputPath
if ($LASTEXITCODE -ne 0) {
    Remove-Item -LiteralPath $outputPath -Force -ErrorAction SilentlyContinue
    throw "pg_dump failed with exit code $LASTEXITCODE."
}

$length = (Get-Item -LiteralPath $outputPath).Length
if ($length -le 0) {
    Remove-Item -LiteralPath $outputPath -Force
    throw "The backup file is empty."
}

Write-Host "Backup complete: $length bytes"
