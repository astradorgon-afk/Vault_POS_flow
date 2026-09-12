#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Generates local development secrets for Pos.Api and stores them in user secrets.

.DESCRIPTION
    Creates an RSA token-signing key, a database password and a bootstrap owner
    password, then writes them to the .NET user-secrets store for the API
    project. Nothing is written into the repository.

    User secrets live in the user profile, outside the working tree, so they
    cannot be committed by accident. Production supplies the same values through
    environment variables from the orchestrator's secret store.

.PARAMETER Force
    Overwrite secrets that are already set. Without this, existing values are
    left alone so re-running the script does not sign you out or orphan your
    local database.

.EXAMPLE
    ./scripts/init-dev-secrets.ps1
#>

[CmdletBinding()]
param(
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repositoryRoot = (Resolve-Path (Join-Path $scriptDirectory '..')).Path
$apiProject = Join-Path $repositoryRoot 'src/Pos.Api/Pos.Api.csproj'

if (-not (Test-Path $apiProject)) {
    throw "Could not find $apiProject. Run this from inside the repository."
}

function Get-ExistingSecrets {
    $raw = & dotnet user-secrets list --project $apiProject --json 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $raw) { return @{} }

    # The CLI wraps its JSON in sentinel lines.
    $json = ($raw | Where-Object { $_ -notmatch '^//' }) -join "`n"
    if ([string]::IsNullOrWhiteSpace($json)) { return @{} }

    try { return ($json | ConvertFrom-Json) } catch { return @{} }
}

function Set-Secret {
    param([string] $Key, [string] $Value)

    & dotnet user-secrets set $Key $Value --project $apiProject | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to set user secret $Key." }
    Write-Host "  set $Key" -ForegroundColor DarkGray
}

function New-RandomPassword {
    param([int] $Bytes = 24)

    $buffer = [byte[]]::new($Bytes)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($buffer)

    # Base64url: safe in a connection string and in a shell without quoting.
    return [Convert]::ToBase64String($buffer).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

Write-Host 'Initialising development secrets for Pos.Api' -ForegroundColor Cyan

& dotnet user-secrets init --project $apiProject | Out-Null
$existing = Get-ExistingSecrets

# --- Token signing key -------------------------------------------------------
if ($Force -or -not $existing.'Jwt:SigningKeyPem') {
    $rsa = [System.Security.Cryptography.RSA]::Create(2048)
    try {
        $pem = $rsa.ExportRSAPrivateKeyPem()
        Set-Secret 'Jwt:SigningKeyPem' $pem
    }
    finally {
        $rsa.Dispose()
    }
}
else {
    Write-Host '  Jwt:SigningKeyPem already set (use -Force to replace)' -ForegroundColor DarkGray
}

# --- Database connection -----------------------------------------------------
if ($Force -or -not $existing.'ConnectionStrings:Postgres') {
    $dbPassword = New-RandomPassword
    $connection = "Host=localhost;Port=5432;Database=vaultflow;Username=pos_app;Password=$dbPassword"
    Set-Secret 'ConnectionStrings:Postgres' $connection

    Write-Host ''
    Write-Host 'Put this password in your .env so the database container agrees:' -ForegroundColor Yellow
    Write-Host "  POS_APP_PASSWORD=$dbPassword"
    Write-Host ''
}
else {
    Write-Host '  ConnectionStrings:Postgres already set (use -Force to replace)' -ForegroundColor DarkGray
}

# --- Bootstrap owner ---------------------------------------------------------
if ($Force -or -not $existing.'BootstrapOwner:Password') {
    $ownerPassword = New-RandomPassword -Bytes 18
    Set-Secret 'BootstrapOwner:Password' $ownerPassword

    Write-Host ''
    Write-Host 'Bootstrap owner account (created only on an empty database):' -ForegroundColor Yellow
    Write-Host '  username: owner'
    Write-Host "  password: $ownerPassword"
    Write-Host '  Sign in, change it, then set BootstrapOwner:Enabled to false.'
    Write-Host ''
}
else {
    Write-Host '  BootstrapOwner:Password already set (use -Force to replace)' -ForegroundColor DarkGray
}

Write-Host 'Done. These values live in the user-secrets store, never in the repository.' -ForegroundColor Green
