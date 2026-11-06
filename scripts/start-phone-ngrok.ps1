#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Opens the local VaultFlow Web UI through ngrok for phone camera testing.
.DESCRIPTION
    Runs ngrok in Docker and prints its trusted HTTPS URL. The ngrok token is
    read from an ignored local env file and never passed as a command argument.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$secretsDir = Join-Path $repo '.secrets'
$envFile = Join-Path $secretsDir 'ngrok.env'
$container = 'vaultflow-phone-ngrok'

try {
    $web = Invoke-WebRequest 'http://localhost:5215/login' -UseBasicParsing -TimeoutSec 8
    $api = Invoke-WebRequest 'http://localhost:5177/health/live' -UseBasicParsing -TimeoutSec 8
    if ($web.StatusCode -ne 200 -or $api.StatusCode -ne 200) { throw 'The local stack is not ready.' }
}
catch { throw 'Start the VaultFlow Web UI and API before opening the ngrok tunnel.' }

& docker info --format '{{.ServerVersion}}' 1>$null
if ($LASTEXITCODE -ne 0) { throw 'Docker Desktop must be running.' }

if (-not (Test-Path $envFile)) {
    New-Item -ItemType Directory -Path $secretsDir -Force | Out-Null
    $token = $env:NGROK_AUTHTOKEN
    if ([string]::IsNullOrWhiteSpace($token)) {
        $secureToken = Read-Host 'Enter your ngrok authtoken' -AsSecureString
        $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureToken)
        try { $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
    }
    if ([string]::IsNullOrWhiteSpace($token)) { throw 'An ngrok authtoken is required.' }
    [IO.File]::WriteAllText($envFile, "NGROK_AUTHTOKEN=$token`n", [Text.Encoding]::ASCII)
    $token = $null
}

if (-not (Select-String -LiteralPath $envFile -Pattern '^NGROK_AUTHTOKEN=\S+$' -Quiet)) {
    throw "The ignored $envFile file needs one NGROK_AUTHTOKEN=<token> line."
}

$running = & docker ps --filter "name=^/${container}$" --format '{{.Names}}'
if ($running -ne $container) {
    $existing = & docker ps --all --filter "name=^/${container}$" --format '{{.Names}}'
    if ($existing -eq $container) {
        & docker rm $container 1>$null
        if ($LASTEXITCODE -ne 0) { throw 'Could not replace the stopped ngrok container.' }
    }
    & docker run --detach --name $container --restart unless-stopped `
        --env-file $envFile `
        --publish '127.0.0.1:4041:4040' `
        ngrok/ngrok:latest http host.docker.internal:5215 1>$null
    if ($LASTEXITCODE -ne 0) { throw 'Could not start the ngrok container.' }
}

$url = $null
for ($attempt = 0; $attempt -lt 15; $attempt++) {
    try {
        $tunnels = (Invoke-RestMethod 'http://127.0.0.1:4041/api/tunnels' -TimeoutSec 3).tunnels
        $url = $tunnels | Where-Object { $_.public_url -like 'https://*' } |
            Select-Object -First 1 -ExpandProperty public_url
        if ($url) { break }
    }
    catch { }
    Start-Sleep -Seconds 1
}
if (-not $url) {
    throw "ngrok did not return a tunnel URL. Check: docker logs $container"
}

Write-Host "Open on your phone: $url/purchasing/new" -ForegroundColor Green
Write-Host 'Sign in, continue past the one-time ngrok notice if shown, then tap Scan with camera.'
Write-Host "Stop when finished: docker stop $container"
