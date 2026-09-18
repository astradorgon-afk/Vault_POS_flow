[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory = $true)]
    [string] $BackupFile
)

$resolvedBackup = [IO.Path]::GetFullPath($BackupFile)
if (-not (Test-Path -LiteralPath $resolvedBackup -PathType Leaf)) {
    throw "Backup file not found: $resolvedBackup"
}

$confirmation = Read-Host "This replaces the vaultflow database. Type RESTORE to continue"
if ($confirmation -cne "RESTORE") {
    throw "Restore cancelled."
}

if ($PSCmdlet.ShouldProcess("vaultflow", "restore $resolvedBackup")) {
    Write-Host "Restoring $resolvedBackup into PostgreSQL..."
    Get-Content -LiteralPath $resolvedBackup -AsByteStream |
        & docker compose exec -T postgres pg_restore -U pos_migrator -d vaultflow --clean --if-exists --no-owner --no-acl
    if ($LASTEXITCODE -ne 0) {
        throw "pg_restore failed with exit code $LASTEXITCODE."
    }
    Write-Host "Restore complete. Run the application smoke checks before reopening traffic."
}
