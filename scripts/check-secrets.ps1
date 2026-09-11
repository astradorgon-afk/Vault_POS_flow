#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Fails if a tracked file looks like it contains a credential.

.DESCRIPTION
    A cheap pre-commit and CI guard. It is not a substitute for a real secret
    scanner, but it catches the common accidents: a filled-in connection string,
    a private key pasted into a config file, a token in an appsettings override.

    Placeholders are allowed by design, so the example files stay useful.
#>

[CmdletBinding()]
param(
    [string] $Root
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Root)) {
    # $PSScriptRoot is not populated while parameter defaults are bound under
    # every host, so the repository root is resolved here instead.
    $scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
    $Root = (Resolve-Path (Join-Path $scriptDirectory '..')).Path
}

$patterns = @(
    @{ Name = 'Private key block';   Pattern = '-----BEGIN (RSA |EC |OPENSSH |PGP )?PRIVATE KEY-----' },
    @{ Name = 'AWS access key';      Pattern = 'AKIA[0-9A-Z]{16}' },
    @{ Name = 'Populated password';  Pattern = '(?i)(password|pwd)\s*[=:]\s*["'']?(?!.*(REPLACE|change-me|placeholder|\$\{|:\?|design|vaultflow-test-only|<|\bEMPTY\b))[^\s"'';,}]{8,}' },
    @{ Name = 'Bearer token';        Pattern = '(?i)bearer\s+[A-Za-z0-9\-._~+/]{24,}' },
    @{ Name = 'Generic api key';     Pattern = '(?i)(api[_-]?key|secret|client[_-]?secret)\s*[=:]\s*["'']?(?!.*(REPLACE|change-me|placeholder|\$\{|:\?))[A-Za-z0-9\-._]{20,}' }
)

# Only files git actually tracks: build output and local .env files are ignored
# by design and scanning them produces noise, not safety.
Push-Location $Root
try {
    $tracked = & git ls-files
    if ($LASTEXITCODE -ne 0) {
        throw 'git ls-files failed; run this from inside the repository.'
    }
}
finally {
    Pop-Location
}

$skipExtensions = @('.png', '.jpg', '.jpeg', '.gif', '.ico', '.pdf', '.zip', '.dll', '.exe', '.woff', '.woff2')
$findings = @()

foreach ($relative in $tracked) {
    if ([string]::IsNullOrWhiteSpace($relative)) { continue }

    $extension = [System.IO.Path]::GetExtension($relative)
    if ($skipExtensions -contains $extension) { continue }

    # This script necessarily contains the patterns it searches for.
    if ($relative -like '*check-secrets.ps1') { continue }

    $path = Join-Path $Root $relative
    if (-not (Test-Path $path)) { continue }

    $content = Get-Content -Path $path -Raw -ErrorAction SilentlyContinue
    if ($null -eq $content) { continue }

    foreach ($rule in $patterns) {
        $match = [regex]::Match($content, $rule.Pattern)
        if ($match.Success) {
            $line = ($content.Substring(0, $match.Index) -split "`n").Count
            $findings += [pscustomobject]@{
                File = $relative
                Line = $line
                Rule = $rule.Name
            }
        }
    }
}

if ($findings.Count -gt 0) {
    Write-Host 'Potential secrets found in tracked files:' -ForegroundColor Red
    $findings | Format-Table -AutoSize | Out-String | Write-Host
    Write-Host 'Remove the value, rotate it, and supply it through user secrets or the environment.' -ForegroundColor Red
    exit 1
}

Write-Host 'No secrets detected in tracked files.' -ForegroundColor Green
exit 0
