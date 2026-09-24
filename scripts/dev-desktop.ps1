#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs the development database, the API and the Blazor Web UI together.

.DESCRIPTION
    The Claude desktop app starts this from .claude/launch.json so the Web UI
    can be previewed with one trigger. It can also be run by hand.

    1. Creates or starts the development PostgreSQL container that the API's
       user secrets point at (ConnectionStrings:Postgres), and waits until it
       accepts connections.
    2. Builds the API, then the Web project. They share Pos.Infrastructure, so
       building them side by side would fight over the same outputs.
    3. Starts the API in the background and waits for /health/live. In
       Development the API applies migrations and seeds data on startup.
    4. Runs the Web UI in the foreground on http://localhost:5215.

    An API that is already healthy, and a Web UI already listening on its port,
    are reused rather than started again. A reused Web UI is never rebuilt, so
    stop it first when the goal is to run the current source. Stopping the Web UI
    stops the API this script started.

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
$webUrl = 'http://localhost:5215'
$webPort = ([uri] $webUrl).Port

function Test-ApiLive {
    try {
        $response = Invoke-WebRequest $apiHealthUrl -UseBasicParsing -TimeoutSec 2
        return $response.StatusCode -eq 200
    }
    catch {
        return $false
    }
}

function Test-PortListening {
    param([int] $Port)

    # Answers the question Kestrel asks when it starts: is anything already
    # accepting connections here? A TCP connect behaves the same in Windows
    # PowerShell 5.1 and PowerShell 7, unlike Invoke-WebRequest, which reports
    # redirects and error statuses differently and offers no usable response
    # when -MaximumRedirection stops a redirect.
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $pending = $client.BeginConnect('127.0.0.1', $Port, $null, $null)
        if (-not $pending.AsyncWaitHandle.WaitOne(1000, $false)) { return $false }
        $client.EndConnect($pending)
        return $true
    }
    catch {
        return $false
    }
    finally {
        $client.Close()
    }
}

function Get-ListeningProcessId {
    param([int] $Port)

    # Only used to name the process holding the port in a message, so a missing
    # NetTCPIP module is not an error.
    try {
        $listener = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction Stop |
            Select-Object -First 1
        if ($null -ne $listener) { return $listener.OwningProcess }
    }
    catch {
    }

    return $null
}

function Start-DevelopmentDatabase {
    # Windows PowerShell 5.1 turns redirected native stderr into terminating
    # errors under 'Stop'; exit codes are checked explicitly instead.
    $ErrorActionPreference = 'Continue'

    if ($null -eq (Get-Command docker -ErrorAction SilentlyContinue)) {
        throw 'Docker CLI was not found. Install Docker Desktop before starting VaultFlow.'
    }

    & docker info --format '{{.ServerVersion}}' 1>$null 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw 'Docker Desktop is not running. Start Docker Desktop, wait until its engine is ready, then run this command again.'
    }

    $state =& docker inspect --format '{{.State.Status}}' $DatabaseContainer 2>$null
    if ($LASTEXITCODE -ne 0) {
        # user-secrets emits a short comment before and after its JSON. Remove
        # only those comment lines; never print the resulting object because it
        # also contains the private JWT signing key.
        $secretOutput = @(& dotnet user-secrets list --json --project $apiProject 2>$null)
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not read the API user secrets. Run scripts/init-dev-secrets.ps1 first.'
        }

        $secretJson = ($secretOutput | Where-Object { -not ([string]$_).TrimStart().StartsWith('//') }) -join "`n"
        try {
            $secrets = $secretJson | ConvertFrom-Json
        }
        catch {
            throw 'The API user secrets could not be read as JSON. Run scripts/init-dev-secrets.ps1 again.'
        }

        $connectionString = $secrets.'ConnectionStrings:Postgres'
        if ([string]::IsNullOrWhiteSpace($connectionString)) {
            throw 'ConnectionStrings:Postgres is missing from the API user secrets. Follow docs/LOCAL_TESTING.md to configure it.'
        }

        $connection = @{}
        foreach ($part in $connectionString.Split(';')) {
            $separator = $part.IndexOf('=')
            if ($separator -gt 0) {
                $connection[$part.Substring(0, $separator).Trim()] = $part.Substring($separator + 1).Trim()
            }
        }

        $hostName = $connection['Host']
        $port = $connection['Port']
        $database = $connection['Database']
        $userName = $connection['Username']
        $password = $connection['Password']

        if ($hostName -notin @('localhost', '127.0.0.1') -or
            [string]::IsNullOrWhiteSpace($port) -or
            [string]::IsNullOrWhiteSpace($database) -or
            [string]::IsNullOrWhiteSpace($userName) -or
            [string]::IsNullOrWhiteSpace($password)) {
            throw 'The development connection string must contain a local Host, Port, Database, Username and Password.'
        }

        Write-Host "Creating $DatabaseContainer on 127.0.0.1:$port" -ForegroundColor Cyan
        & docker run --detach --name $DatabaseContainer `
            --env "POSTGRES_DB=$database" `
            --env "POSTGRES_USER=$userName" `
            --env "POSTGRES_PASSWORD=$password" `
            --publish "127.0.0.1:${port}:5432" `
            --volume "${DatabaseContainer}data:/var/lib/postgresql/data" `
            postgres:17-alpine | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Could not create $DatabaseContainer. Check Docker Desktop and whether port $port is already in use."
        }

        $state = 'running'
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
    # A Web UI left over from an earlier run keeps serving after its terminal is
    # closed, so bind the port once instead of failing: reuse the running
    # instance. Rebuilding it would only produce bits that instance never loads,
    # so skip the build as well.
    if (Test-PortListening $webPort) {
        $owner = Get-ListeningProcessId $webPort
        $ownerText = if ($null -eq $owner) { '' } else { " (process $owner)" }
        Write-Host "Reusing the Web UI already running on $webUrl$ownerText" -ForegroundColor DarkGray
        Write-Host 'It was not rebuilt. Stop it and run this script again to build the current source.' -ForegroundColor DarkGray

        if (-not (Test-ApiLive)) {
            Write-Host 'The API on http://localhost:5177 is not answering, so the reused Web UI cannot load data until it runs.' -ForegroundColor Yellow
        }

        # Stay attached while it serves, so callers such as the Claude desktop
        # preview see the stack as running. Ctrl+C here leaves it untouched.
        while (Test-PortListening $webPort) { Start-Sleep -Seconds 5 }
        Write-Host "The Web UI on $webUrl stopped." -ForegroundColor DarkGray
        return
    }

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
