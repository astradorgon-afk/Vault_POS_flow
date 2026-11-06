#Requires -Version 5.1
<#
.SYNOPSIS
    Runs a published offline PWA through an HTTPS ngrok tunnel for phone testing.
.DESCRIPTION
    Publishes and hosts the release PWA locally, then exposes it through the
    ngrok agent. Keep the tunnel running: its free-tier address changes on
    restart and visitors see ngrok's one-time notice. A permanent deployment
    needs a stable domain. The authtoken is read from an ignored local env file
    and never passed as a command argument.
#>
[CmdletBinding()]
param([switch]$SkipPublish, [switch]$ReuseTunnel)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$pilot = Join-Path $repo 'artifacts/phone-pwa'
$publish = Join-Path $pilot 'publish'
$tools = Join-Path $pilot 'tools'
$pilotStateFile = Join-Path $pilot 'pilot.json'
$ngrokEnvFile = Join-Path (Join-Path $repo '.secrets') 'ngrok.env'
$ngrokExe = Join-Path $tools 'ngrok.exe'
New-Item -ItemType Directory -Path $pilot, $tools -Force | Out-Null
$existingPilot = $null
if ($ReuseTunnel) {
    if (-not (Test-Path $pilotStateFile)) { throw 'No recorded phone tunnel exists to reuse.' }
    $existingPilot = Get-Content $pilotStateFile -Raw | ConvertFrom-Json
    $existingTunnel = Get-CimInstance Win32_Process -Filter "ProcessId=$($existingPilot.TunnelProcessId)"
    if (-not $existingTunnel -or $existingTunnel.ExecutablePath -ne $ngrokExe -or
        $existingPilot.Url -notmatch '^https://[a-z0-9-]+\.ngrok-free\.(dev|app)$') {
        throw 'The recorded test tunnel is no longer running. Start a new pilot without -ReuseTunnel.'
    }
    $existingWeb = Get-CimInstance Win32_Process -Filter "ProcessId=$($existingPilot.WebProcessId)"
    if ($existingWeb -and $existingWeb.CommandLine -notmatch 'Pos\.Web\.dll --urls http://localhost:5321') {
        throw 'The recorded web PID belongs to a different process. Stop it manually before restarting.'
    }
    if (-not $SkipPublish) {
        # Publish to a new directory while the current host stays available.
        $publish = Join-Path $pilot ('publish-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    }
    elseif ($existingPilot.PublishPath) {
        $publish = $existingPilot.PublishPath
    }
}
$listener = Get-NetTCPConnection -LocalPort 5321 -State Listen -ErrorAction SilentlyContinue
if ($listener -and (-not $ReuseTunnel -or $listener.OwningProcess -ne $existingPilot.WebProcessId)) {
    throw 'Port 5321 is in use by an unexpected process.'
}
$api = Invoke-WebRequest 'http://localhost:5177/health/live' -UseBasicParsing -TimeoutSec 8
if ($api.StatusCode -ne 200) { throw 'Start the VaultFlow API before starting the PWA.' }
if (-not $ReuseTunnel -and (Test-Path $pilotStateFile)) {
    # Fail before publishing or starting anything when a prior tunnel is still up.
    $prior = Get-Content $pilotStateFile -Raw | ConvertFrom-Json
    $priorTunnel = if ($prior.TunnelProcessId) {
        Get-CimInstance Win32_Process -Filter "ProcessId=$($prior.TunnelProcessId)" -ErrorAction SilentlyContinue
    }
    if ($priorTunnel -and $priorTunnel.ExecutablePath -eq $ngrokExe) {
        throw "A previous ngrok tunnel is still running (PID $($prior.TunnelProcessId)). Stop it or reuse it with -ReuseTunnel."
    }
}
if (-not $SkipPublish) {
    & dotnet publish (Join-Path $repo 'src/Pos.Web/Pos.Web.csproj') -c Release -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'PWA publish failed.' }
}
$worker = Join-Path $publish 'wwwroot/service-worker.js'
if (-not (Test-Path $worker) -or -not (Select-String -LiteralPath $worker -Pattern 'vaultflow-root-shell' -Quiet)) {
    throw 'Publish the release PWA first. A development build cannot be used for this pilot.'
}
if ($ReuseTunnel) {
    if ($existingWeb) {
        Stop-Process -Id $existingWeb.ProcessId -ErrorAction Stop
        for ($attempt = 0; $attempt -lt 30; $attempt++) {
            if (-not (Get-NetTCPConnection -LocalPort 5321 -State Listen -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Milliseconds 500
        }
        if (Get-NetTCPConnection -LocalPort 5321 -State Listen -ErrorAction SilentlyContinue) {
            throw 'The old PWA host did not release port 5321.'
        }
    }
}
if (-not (Test-Path $ngrokExe)) {
    $download = Join-Path $tools 'ngrok.zip'
    Invoke-WebRequest 'https://bin.equinox.io/c/bNyj1mQVY4c/ngrok-v3-stable-windows-amd64.zip' -OutFile $download
    Expand-Archive -LiteralPath $download -DestinationPath $tools -Force
    Remove-Item -LiteralPath $download -Force
    if (-not (Test-Path $ngrokExe)) { throw "The ngrok download did not contain ngrok.exe in $tools." }
    & $ngrokExe --version
    if ($LASTEXITCODE -ne 0) { throw 'The ngrok executable download is incomplete.' }
}
$previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
try {
    $env:ASPNETCORE_ENVIRONMENT = 'Production'
    $web = Start-Process dotnet -ArgumentList 'Pos.Web.dll', '--urls', 'http://localhost:5321', '--Api:BaseAddress', 'http://localhost:5177' -WorkingDirectory $publish -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $pilot 'web.log') -RedirectStandardError (Join-Path $pilot 'web-error.log')
}
finally { $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment }
$webReady = $false
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    if ($web.HasExited) { throw "PWA host stopped. Read $pilot/web-error.log." }
    try {
        $response = Invoke-WebRequest 'http://localhost:5321/service-worker.js' -UseBasicParsing -TimeoutSec 3
        if ($response.StatusCode -eq 200 -and $response.Content -match 'vaultflow-root-shell') { $webReady = $true; break }
    } catch { }
    Start-Sleep -Seconds 1
}
if (-not $webReady) { Stop-Process -Id $web.Id; throw 'PWA host did not become ready.' }
if ($ReuseTunnel) {
    $existingPilot.WebProcessId = $web.Id
    $existingPilot | Add-Member -NotePropertyName PublishPath -NotePropertyValue $publish -Force
    $existingPilot | ConvertTo-Json | Set-Content $pilotStateFile
    Write-Host "Updated phone address: $($existingPilot.Url)/login" -ForegroundColor Green
    Write-Host "Web host restarted. Stop host when finished: Stop-Process -Id $($web.Id)"
    exit
}
if (-not (Test-Path $ngrokEnvFile)) {
    New-Item -ItemType Directory -Path (Split-Path $ngrokEnvFile) -Force | Out-Null
    $token = $env:NGROK_AUTHTOKEN
    if ([string]::IsNullOrWhiteSpace($token)) {
        $secureToken = Read-Host 'Enter your ngrok authtoken' -AsSecureString
        $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureToken)
        try { $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
    }
    if ([string]::IsNullOrWhiteSpace($token)) { throw 'An ngrok authtoken is required.' }
    [IO.File]::WriteAllText($ngrokEnvFile, "NGROK_AUTHTOKEN=$token`n", [Text.Encoding]::ASCII)
    $token = $null
}
if (-not (Select-String -LiteralPath $ngrokEnvFile -Pattern '^NGROK_AUTHTOKEN=\S+$' -Quiet)) {
    throw "The ignored $ngrokEnvFile file needs one NGROK_AUTHTOKEN=<token> line."
}
$ngrokToken = (Select-String -LiteralPath $ngrokEnvFile -Pattern '^NGROK_AUTHTOKEN=(\S+)$').Matches[0].Groups[1].Value

$tunnelLog = Join-Path $pilot 'tunnel.log'
# The agent reads NGROK_AUTHTOKEN from the environment; never pass it as an argument.
$previousToken = $env:NGROK_AUTHTOKEN
$env:NGROK_AUTHTOKEN = $ngrokToken
try {
    $tunnel = Start-Process $ngrokExe -ArgumentList 'http', '5321' -WindowStyle Hidden -PassThru -RedirectStandardOutput $tunnelLog -RedirectStandardError (Join-Path $pilot 'tunnel-error.log')
}
finally { $env:NGROK_AUTHTOKEN = $previousToken }
$url = $null
for ($attempt = 0; $attempt -lt 40; $attempt++) {
    if ($tunnel.HasExited) { throw "Tunnel stopped. Read the tunnel logs in $pilot. Web host PID: $($web.Id)." }
    try {
        $tunnels = (Invoke-RestMethod 'http://127.0.0.1:4040/api/tunnels' -TimeoutSec 3).tunnels
        $url = $tunnels | Where-Object { $_.public_url -like 'https://*' } |
            Select-Object -First 1 -ExpandProperty public_url
        if ($url) { break }
    }
    catch { }
    Start-Sleep -Seconds 1
}
if (-not $url) { throw "Tunnel did not return an address. Read the tunnel logs in $pilot. Web host PID: $($web.Id)." }
@{ Url = $url; WebProcessId = $web.Id; TunnelProcessId = $tunnel.Id; PublishPath = $publish } | ConvertTo-Json | Set-Content $pilotStateFile
Write-Host "Install on your phone: $url/login" -ForegroundColor Green
Write-Host 'Continue past the one-time ngrok notice. Sign in online once to prepare saved work, then reopen /login offline.'
Write-Host 'Keep this tunnel running. Do not clear site data while work is pending.'
Write-Host "Stop when finished: Stop-Process -Id $($web.Id),$($tunnel.Id)"
