#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Back up the VaultFlow PostgreSQL database using pg_basebackup.

.DESCRIPTION
    Creates a full base backup of the PostgreSQL database with WAL archiving.
    Backups are stored in the target directory with a timestamp. The script
    supports incremental backup with WAL files or full base backups.

    This script is designed for production use. A restore drill (verifying
    the backup in a scratch container) should be performed monthly.

.PARAMETER Host
    PostgreSQL host. Default: localhost

.PARAMETER Port
    PostgreSQL port. Default: 5432

.PARAMETER Username
    PostgreSQL superuser. Default: pos_migrator

.PARAMETER Database
    Database name (for connection only). Default: vaultflow

.PARAMETER BackupDir
    Directory to store backups. Default: ./backups
    Directory is created if it does not exist.

.PARAMETER Format
    Backup format: 'plain' (directory) or 'tar' (compressed).
    Default: tar

.PARAMETER Compression
    Compression method (tar format only): 'none', 'gzip', 'bzip2', or 'zstd'.
    Default: zstd

.PARAMETER Label
    Backup label. Default: vaultflow-{timestamp}

.PARAMETER Password
    PostgreSQL password. If not provided, reads from PGPASSWORD environment variable
    or prompts interactively.

.EXAMPLE
    # Backup to default location with zstd compression
    ./backup.ps1

.EXAMPLE
    # Backup to custom location, plain format
    ./backup.ps1 -BackupDir /mnt/backups -Format plain

.EXAMPLE
    # Scheduled nightly backup (suppress prompts)
    $env:PGPASSWORD = "..." ; ./backup.ps1 -Host prod.db.internal
#>

param(
    [string] $Host = 'localhost',
    [int] $Port = 5432,
    [string] $Username = 'pos_migrator',
    [string] $Database = 'vaultflow',
    [string] $BackupDir = './backups',
    [ValidateSet('plain', 'tar')] [string] $Format = 'tar',
    [ValidateSet('none', 'gzip', 'bzip2', 'zstd')] [string] $Compression = 'zstd',
    [string] $Label,
    [string] $Password
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Default label: vaultflow-{yyyyMMdd-HHmmss}
if (-not $Label) {
    $Label = "vaultflow-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
}

# Ensure backup directory exists
if (-not (Test-Path $BackupDir)) {
    New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null
    Write-Host "Created backup directory: $BackupDir"
}

# Set password for pg_basebackup
if ($Password) {
    $env:PGPASSWORD = $Password
}
elseif (-not $env:PGPASSWORD) {
    # Prompt for password if not provided and not in environment
    $SecurePassword = Read-Host "Enter PostgreSQL password for $Username" -AsSecureString
    $env:PGPASSWORD = [System.Net.NetworkCredential]::new('', $SecurePassword).Password
}

# Build pg_basebackup arguments
$Arguments = @(
    '--host', $Host
    '--port', $Port
    '--username', $Username
    '--dbname', $Database
    '--label', $Label
    '--progress'
    '--verbose'
    '--format', $Format
    '--compression', $Compression
    '--checkpoint', 'fast'    # Use fast checkpoint for faster backup start
    '--backup-path', $BackupDir
)

# Add output file path if using tar format
if ($Format -eq 'tar') {
    $BackupFile = Join-Path $BackupDir "$Label.tar"
    switch ($Compression) {
        'gzip' { $BackupFile += '.gz' }
        'bzip2' { $BackupFile += '.bz2' }
        'zstd' { $BackupFile += '.zst' }
    }
    $Arguments += '--file', $BackupFile
}

try {
    Write-Host "Starting backup: $Label"
    Write-Host "Backup format: $Format"
    Write-Host "Compression: $Compression"
    Write-Host "Target: $BackupDir"
    Write-Host ""

    # Run pg_basebackup
    & pg_basebackup @Arguments

    if ($LASTEXITCODE -ne 0) {
        throw "pg_basebackup failed with exit code $LASTEXITCODE"
    }

    Write-Host ""
    Write-Host "✓ Backup completed successfully: $Label"

    # Show backup size and location
    if ($Format -eq 'tar') {
        $FileSize = (Get-Item $BackupFile).Length / 1MB
        Write-Host "  File: $BackupFile"
        Write-Host "  Size: $([Math]::Round($FileSize, 2)) MB"
    }
    else {
        $DirSize = (Get-ChildItem $BackupDir -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB
        Write-Host "  Directory: $(Join-Path $BackupDir $Label)"
        Write-Host "  Size: $([Math]::Round($DirSize, 2)) MB"
    }

    Write-Host ""
    Write-Host "REMEMBER: An untested backup is not a backup. Schedule a monthly"
    Write-Host "restore drill to verify the backup can be recovered."
}
catch {
    Write-Error "Backup failed: $_"
    exit 1
}
finally {
    # Clear password from environment
    Remove-Item env:PGPASSWORD -ErrorAction SilentlyContinue
}
