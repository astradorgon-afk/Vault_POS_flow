#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs the development database, the API and the Blazor Web UI together.

.DESCRIPTION
    The Claude desktop app starts this from .claude/launch.json so the Web UI
    can be previewed with one trigger. It can also be run by hand.

    1. Starts the development PostgreSQL container that the API's user secrets
       point at (ConnectionStrings:Postgres), and waits until it accepts
       connections.
    2. Builds the API, then the Web project. They share Pos.Infrastructure, so
       building them side by side would fight over the same outputs.
    3. Starts the API in the background and waits for /health/live. In
       Development the API applies migrations and seeds data on startup.
    4. Runs the Web UI in the foreground on http://localhost:5215.

    An API that is already healthy is reused rather than started again. Stopping
    the Web UI stops the API this script started.

    Runs under Windows PowerShell 5.1 and PowerShell 7 alike.

.PARAMETER DatabaseContainer
    The Docker container holding the development database.

.EXAMPLE
    ./scripts/dev-desktop.ps1
#>

[CmdletBinding()]
param(
    [string] $DatabaseContainer = 'vaultflow-dev-pg',
    [int] $ApiStartupSeconds = 180
)

$ErrorActionPreference = 'Stop'

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repositoryRoot = (Resolve-Path (Join-Path $scriptDirectory '..')).Path
$apiProject = Join-Path $repositoryRoot 'src/Pos.Api/Pos.Api.csproj'
$webProject = Join-Path $repositoryRoot 'src/Pos.Web/Pos.Web.csproj'

# The http profiles in each project's launchSettings.json own these ports.
$apiHealthUrl = 'http://localhost:5177/health/live'

function Test-ApiLive {
    try {
        $response = Invoke-WebRequest $apiHealthUrl -UseBasicParsing -TimeoutSec 2
        return $response.StatusCode -eq 200
    }
    catch {
        return $false
    }
}

function Start-DevelopmentDatabase {
    # Windows PowerShell 5.1 turns redirected native stderr into terminating
    # errors under 'Stop'; exit codes are checked explicitly instead.
    $ErrorActionPreference = 'Continue'

    $state =& docker inspect --format '{{.State.Status}}' $DatabaseContainer 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Container $DatabaseContainer does not exist. The API will use whatever ConnectionStrings:Postgres in its user secrets points at."
        return
    }

    if ($state -ne 'running') {
        Write-Host "Starting $DatabaseContainer" -ForegroundColor Cyan
        & docker start $DatabaseContainer | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not start $DatabaseContainer. Is Docker Desktop running?" }
    }

    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        & docker exec $DatabaseContainer pg_isready -q 2>$null
        if ($LASTEXITCODE -eq 0) { return }
        Start-Sleep -Seconds 1
    }

    throw "$DatabaseContainer did not accept connections within 30 seconds."
}

function Invoke-Build {
    param([string] $Project)

    Write-Host "Building $(Split-Path -Leaf $Project)" -ForegroundColor Cyan
    & dotnet build $Project --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $Project" }
}

$api = $null
try {
    if (Test-ApiLive) {
        Write-Host 'Reusing the API already running on http://localhost:5177' -ForegroundColor DarkGray
    }
    else {
        Start-DevelopmentDatabase
        Invoke-Build $apiProject
        Invoke-Build $webProject

        Write-Host 'Starting the API on http://localhost:5177' -ForegroundColor Cyan
        $api = Start-Process dotnet -NoNewWindow -PassThru -WorkingDirectory $repositoryRoot `
            -ArgumentList @('run', '--project', "`"$apiProject`"", '--no-build', '--launch-profile', 'http')

        $deadline = (Get-Date).AddSeconds($ApiStartupSeconds)
        while (-not (Test-ApiLive)) {
            if ($api.HasExited) { throw "The API exited with code $($api.ExitCode) before it became healthy." }
            if ((Get-Date) -gt $deadline) { throw "The API did not become healthy within $ApiStartupSeconds seconds." }
            Start-Sleep -Seconds 2
        }
    }

    if ($null -eq $api) { Invoke-Build $webProject }

    Write-Host 'Starting the Web UI on http://localhost:5215' -ForegroundColor Green
    & dotnet run --project $webProject --no-build --launch-profile http
}
finally {
    if ($null -ne $api -and -not $api.HasExited) {
        # dotnet run hosts the app in a child process; stop the whole tree.
        & taskkill /PID $api.Id /T /F | Out-Null
    }
}
