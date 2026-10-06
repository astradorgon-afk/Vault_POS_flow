#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Starts local HTTPS for Android camera testing over the same Wi-Fi.
.DESCRIPTION
    Proxies the running VaultFlow Web UI on port 5215 through Caddy on port 8444.
    Caddy keeps its private CA key in a Docker volume; only the public root
    certificate is available on port 8445 for installation on the phone.
    Recreates the named container to apply IP or proxy configuration changes.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$config = Join-Path $PSScriptRoot 'Caddyfile.phone'
$container = 'vaultflow-phone-https'
$lan = Get-NetIPConfiguration -ErrorAction Stop |
    Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' } |
    Select-Object -First 1
$ip = $lan.IPv4Address.IPAddress | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($ip)) { throw 'No active LAN IPv4 address was found.' }
$networkProfile = Get-NetConnectionProfile -InterfaceAlias $lan.InterfaceAlias -ErrorAction Stop
$firewallProfile = if ($networkProfile.NetworkCategory -eq 'Public') { 'Public' } else { 'Private' }

try {
    $web = Invoke-WebRequest 'http://localhost:5215/login' -UseBasicParsing -TimeoutSec 8
    $api = Invoke-WebRequest 'http://localhost:5177/health/live' -UseBasicParsing -TimeoutSec 8
    if ($web.StatusCode -ne 200 -or $api.StatusCode -ne 200) { throw 'The local stack is not ready.' }
}
catch { throw 'Start the VaultFlow Web UI and API before starting phone HTTPS.' }

& docker info --format '{{.ServerVersion}}' 1>$null
if ($LASTEXITCODE -ne 0) { throw 'Docker Desktop must be running.' }

$existing = & docker ps --all --filter "name=^/${container}$" --format '{{.Names}}'
if ($existing -eq $container) {
    & docker rm --force $container 1>$null
    if ($LASTEXITCODE -ne 0) { throw 'Could not replace the existing proxy.' }
}
& docker run --detach --name $container --restart unless-stopped `
    --publish '8444:8444' --publish '8445:8445' `
    --env "VAULTFLOW_PHONE_IP=$ip" `
    --volume "${config}:/etc/caddy/Caddyfile:ro" `
    --volume 'vaultflow-phone-caddy-data:/data' `
    caddy:2-alpine 1>$null
if ($LASTEXITCODE -ne 0) { throw 'Could not start the HTTPS proxy.' }

foreach ($entry in @(@('VaultFlow phone HTTPS', 8444), @('VaultFlow phone certificate', 8445))) {
    $name = "$([string]$entry[0]) ($firewallProfile)"
    $port = [int]$entry[1]
    if (-not (Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue)) {
        try {
            New-NetFirewallRule -DisplayName $name -Direction Inbound -Action Allow `
                -Protocol TCP -LocalPort $port -Profile $firewallProfile `
                -RemoteAddress LocalSubnet -ErrorAction Stop | Out-Null
        }
        catch {
            Write-Warning "Port $port may need a $firewallProfile-network firewall rule. Run this in Administrator PowerShell: New-NetFirewallRule -DisplayName '$name' -Direction Inbound -Action Allow -Protocol TCP -LocalPort $port -Profile $firewallProfile -RemoteAddress LocalSubnet"
        }
    }
}

$publicCert = Join-Path $repo '.secrets/vaultflow-phone-ca.crt'
if (-not (Test-Path (Split-Path $publicCert))) {
    New-Item -ItemType Directory -Path (Split-Path $publicCert) | Out-Null
}
$rootReady = $false
for ($attempt = 0; $attempt -lt 10; $attempt++) {
    & docker exec $container test -f /data/caddy/pki/authorities/local/root.crt
    if ($LASTEXITCODE -eq 0) { $rootReady = $true; break }
    Start-Sleep -Seconds 1
}
if (-not $rootReady) { throw 'The HTTPS proxy did not generate its public CA certificate.' }
& docker cp "${container}:/data/caddy/pki/authorities/local/root.crt" $publicCert
if ($LASTEXITCODE -ne 0) { throw 'Could not export the public CA certificate.' }

Write-Host "VaultFlow on Android: https://${ip}:8444" -ForegroundColor Green
Write-Host "Public certificate: http://${ip}:8445/phone-ca.crt" -ForegroundColor Cyan
Write-Host "Public certificate file: $publicCert" -ForegroundColor Cyan
Write-Host 'Install that CA certificate in Android Settings, then reopen the HTTPS address in Chrome.'
