#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Generates local development secrets for VaultFlow.

.DESCRIPTION
    Creates the values a developer needs to run the stack, without writing any
    of them into tracked files:

    - An RSA token-signing key, stored in the API's .NET user-secrets store (for
      `dotnet run`) and in .secrets/jwt-signing-key.pem (the Docker secret the
      compose stack mounts). The .secrets directory is ignored by git.
    - The three database passwords compose needs. When .env does not exist yet
      it is created with them; an existing .env is never overwritten.

    Runs under Windows PowerShell 5.1 and PowerShell 7 alike.

.PARAMETER Force
    Replace a signing key that already exists. Without this, existing values are
    left alone so re-running the script does not sign everyone out.

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
$secretsDirectory = Join-Path $repositoryRoot '.secrets'
$signingKeyFile = Join-Path $secretsDirectory 'jwt-signing-key.pem'
$envFile = Join-Path $repositoryRoot '.env'

if (-not (Test-Path $apiProject)) {
    throw "Could not find $apiProject. Run this from inside the repository."
}

function New-RandomPassword {
    param([int] $Bytes = 24)

    # RNGCryptoServiceProvider exists on .NET Framework and .NET alike.
    $buffer = New-Object byte[] $Bytes
    $generator = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
    try { $generator.GetBytes($buffer) } finally { $generator.Dispose() }

    # Base64url: safe in a connection string and in a shell without quoting.
    return [Convert]::ToBase64String($buffer).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function New-SigningKeyPem {
    # .NET 5+ exports PEM directly; Windows PowerShell 5.1 runs on .NET Framework,
    # which cannot, so the PKCS#1 structure is DER-encoded by hand there.
    $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider 2048
    try {
        if ($rsa.PSObject.Methods.Name -contains 'ExportRSAPrivateKeyPem') {
            return $rsa.ExportRSAPrivateKeyPem()
        }

        $p = $rsa.ExportParameters($true)
        $body = New-Object System.Collections.Generic.List[byte]
        foreach ($part in @([byte[]]@(0), $p.Modulus, $p.Exponent, $p.D, $p.P, $p.Q, $p.DP, $p.DQ, $p.InverseQ)) {
            $body.AddRange([byte[]](ConvertTo-DerInteger $part))
        }

        $der = [byte[]](@(0x30) + (ConvertTo-DerLength $body.Count) + $body.ToArray())
        $base64 = [Convert]::ToBase64String($der)
        $lines = for ($i = 0; $i -lt $base64.Length; $i += 64) { $base64.Substring($i, [Math]::Min(64, $base64.Length - $i)) }

        return "-----BEGIN RSA PRIVATE KEY-----`n" + ($lines -join "`n") + "`n-----END RSA PRIVATE KEY-----"
    }
    finally {
        $rsa.Dispose()
    }
}

function ConvertTo-DerLength {
    param([int] $Length)

    if ($Length -lt 0x80) { return [byte[]]@($Length) }

    $bytes = New-Object System.Collections.Generic.List[byte]
    while ($Length -gt 0) { $bytes.Insert(0, [byte]($Length -band 0xFF)); $Length = $Length -shr 8 }

    return [byte[]](@([byte](0x80 -bor $bytes.Count)) + $bytes.ToArray())
}

function ConvertTo-DerInteger {
    param([byte[]] $Value)

    # Unsigned big-endian: strip leading zeros, then add one back if the high bit is set.
    $start = 0
    while ($start -lt $Value.Length - 1 -and $Value[$start] -eq 0) { $start++ }
    $trimmed = [byte[]]$Value[$start..($Value.Length - 1)]
    if ($trimmed[0] -band 0x80) { $trimmed = [byte[]](@(0) + $trimmed) }

    return [byte[]](@(0x02) + (ConvertTo-DerLength $trimmed.Length) + $trimmed)
}

function Set-UserSecret {
    param([string] $Key, [string] $Value)

    # Piped as JSON so a multi-line value survives Windows argument quoting.
    $json = @{ $Key = $Value } | ConvertTo-Json -Compress
    $temporary = [System.IO.Path]::GetTempFileName()
    try {
        [System.IO.File]::WriteAllText($temporary, $json, (New-Object System.Text.UTF8Encoding $false))
        cmd /c "type `"$temporary`" | dotnet user-secrets set --project `"$apiProject`"" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Failed to set user secret $Key." }
    }
    finally {
        Remove-Item $temporary -Force -ErrorAction SilentlyContinue
    }

    Write-Host "  set user secret $Key" -ForegroundColor DarkGray
}

Write-Host 'Initialising development secrets for VaultFlow' -ForegroundColor Cyan
& dotnet user-secrets init --project $apiProject | Out-Null

# --- Token signing key ----------------------------------------------------------
if ($Force -or -not (Test-Path $signingKeyFile)) {
    New-Item -ItemType Directory -Force $secretsDirectory | Out-Null
    $pem = New-SigningKeyPem
    [System.IO.File]::WriteAllText($signingKeyFile, $pem, (New-Object System.Text.UTF8Encoding $false))
    Write-Host "  wrote .secrets/jwt-signing-key.pem" -ForegroundColor DarkGray
    Set-UserSecret 'Jwt:SigningKeyPem' $pem
}
else {
    Write-Host '  .secrets/jwt-signing-key.pem already exists (use -Force to replace)' -ForegroundColor DarkGray
}

# --- Compose passwords ----------------------------------------------------------
if (-not (Test-Path $envFile)) {
    $content = @(
        '# Local development values generated by scripts/init-dev-secrets.ps1. Never commit.'
        "POSTGRES_PASSWORD=$(New-RandomPassword)"
        "POS_APP_PASSWORD=$(New-RandomPassword)"
        "POS_READONLY_PASSWORD=$(New-RandomPassword)"
    ) -join "`n"

    [System.IO.File]::WriteAllText($envFile, $content + "`n", (New-Object System.Text.UTF8Encoding $false))
    Write-Host '  wrote .env with fresh database passwords' -ForegroundColor DarkGray
}
else {
    Write-Host '  .env already exists; left unchanged' -ForegroundColor DarkGray
}

Write-Host ''
Write-Host 'Done. Start the whole stack with:  docker compose up -d --build' -ForegroundColor Green
Write-Host 'Seeded staff accounts use the development password DevVaultFlow!2026.' -ForegroundColor Green
