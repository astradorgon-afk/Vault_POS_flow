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
    # A committed key has a body. The header on its own is how
    # scripts/init-dev-secrets.ps1 writes a key it generates and never stores,
    # and matching that taught the scan to cry wolf on every run.
    @{ Name = 'Private key block'
       Pattern = '-----BEGIN (RSA |EC |OPENSSH |PGP )?PRIVATE KEY-----[\r\n]+[A-Za-z0-9+/=\r\n]{40,}' },
    @{ Name = 'AWS access key'; Pattern = 'AKIA[0-9A-Z]{16}' },
    # The colon exclusion keeps psql variable references such as
    #   ALTER ROLE ... PASSWORD :'app_password'
    # from reading as literal credentials.
    @{ Name = 'Populated password'
       Pattern = '(?i)(password|pwd)\s*(?:=|:(?!\s*''))\s*["'']?(?<value>[^\s"'';,}]{8,})'
       Assignment = $true },
    @{ Name = 'Bearer token'; Pattern = '(?i)bearer\s+(?<value>[A-Za-z0-9\-._~+/]{24,})' },
    @{ Name = 'Generic api key'
       Pattern = '(?i)(api[_-]?key|secret|client[_-]?secret)\s*[=:]\s*["'']?(?<value>[A-Za-z0-9\-._]{20,})'
       Assignment = $true }
)

# A value that is plainly code rather than a literal: a call, a member access, a
# shell or MSBuild expansion. This is how a repository refers to a credential it
# does not contain — `password: PosApiFactory.TestPassword`,
# `Password = RawKey(key)`, `PASSWORD=$(New-RandomPassword)` — and flagging those
# is what taught everyone to ignore this scan. It found 40 of them and no
# secrets. It applies only to the assignment rules: a JWT is dots and segments
# and would read as a member access, which is the one thing `Bearer` is for.
$codeShaped = '[($]|^[A-Za-z_][A-Za-z0-9_]*\.'

# Values that say, in the value itself, that they are not a credential: the
# placeholders the example files keep, and the strings a negative test uses. Any
# value that contains the word `password` is one of those — a real one does not
# announce itself — and `correct-horse` and `whatever` are the fakes the
# authentication tests sign in with. `DevVaultFlow!2026` is the documented
# development-only account password (DevelopmentDataSeeder), deliberately in the
# repository.
$placeholder = '(?i)(REPLACE|change-me|placeholder|\$\{|:\?|design|correct-horse|vaultflow-test-only|<|\bEMPTY\b|password|DevVaultFlow|\bwhatever\b)'

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
        foreach ($match in [regex]::Matches($content, $rule.Pattern)) {
            $value = $match.Groups['value'].Value

            if ($value -and $value -match $placeholder) {
                continue
            }

            if ($value -and $rule.Assignment -and $value -match $codeShaped) {
                continue
            }

            $line = ($content.Substring(0, $match.Index) -split "`n").Count
            $findings += [pscustomobject]@{
                File = $relative
                Line = $line
                Rule = $rule.Name
            }

            # One report per file and rule is enough to fail the build and send
            # somebody to look; a file with fifty is not fifty problems.
            break
        }
    }
}

if ($findings.Count -gt 0) {
    Write-Host 'Potential secrets found in tracked files:' -ForegroundColor Red
    foreach ($finding in $findings) {
        Write-Host ('  {0}:{1}  {2}' -f $finding.File, $finding.Line, $finding.Rule)
    }
    Write-Host 'Remove the value, rotate it, and supply it through user secrets or the environment.' -ForegroundColor Red
    exit 1
}

Write-Host 'No secrets detected in tracked files.' -ForegroundColor Green
exit 0
