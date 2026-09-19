# Local testing

This is the current Phase 18 focus. Production-only release work is deferred
until the local API, Web UI, database migrations, synchronization flows and
backup/restore path are accepted.

## Prerequisites

- .NET 10 SDK from `global.json`
- Docker Desktop with Linux containers enabled
- PowerShell 7 for the helper scripts

The MAUI Android/Windows workloads are not required for local API/Web testing.

## Start the local stack

From the repository root:

```powershell
Copy-Item .env.example .env
.\scripts\init-dev-secrets.ps1
docker compose up -d --build
docker compose ps
```

The development stack runs PostgreSQL, the migrator, grants setup, API, Web,
Caddy, and the optional development tools described in `docs/DEPLOYMENT.md`.
The site is available at `https://localhost`; the development seed password is
documented in the deployment guide.

## Verify the system

```powershell
Invoke-WebRequest https://localhost/health/live -SkipCertificateCheck
Invoke-WebRequest https://localhost/health/ready -SkipCertificateCheck
dotnet test VaultFlow.slnx --configuration Release
```

The full test command exercises the Docker-backed PostgreSQL suites when Docker
is available. The Phase 17 baseline passed 1,144 tests with no failures or
skips.

## Run the Blazor Web UI

The owner/admin dashboard is the `Pos.Web` Blazor application. It runs at
`http://localhost:5215` and calls the development API at
`http://localhost:5177`.

From the repository root, run:

```powershell
.\scripts\dev-desktop.ps1
```

Despite its historical filename, this starts the API and Blazor Web UI; it
does not start the MAUI desktop register. Open `http://localhost:5215` after
the command reports that the Web UI is running.

The helper creates `vaultflow-dev-pg` automatically from the API's configured
`ConnectionStrings:Postgres` user secret and stores its data in the
`vaultflow-dev-pgdata` Docker volume. Docker Desktop must be running. On a fresh
machine, initialize the secret and signing key first:

```powershell
.\scripts\init-dev-secrets.ps1
$dbPassword = 'VaultFlowDevOnly-ChangeMe-2026'
dotnet user-secrets set 'ConnectionStrings:Postgres' `
  "Host=localhost;Port=15432;Database=vaultflow;Username=pos_migrator;Password=$dbPassword" `
  --project src/Pos.Api/Pos.Api.csproj
```

The API applies migrations and seeds the development users on first start.

## Run the desktop register (Windows)

The desktop register is the .NET MAUI Blazor Hybrid app in `src/Pos.Client`. It
needs the MAUI Windows workload and talks to the API directly, so start the API
first. `scripts/dev-desktop.ps1` does that in one step: it starts the
`vaultflow-dev-pg` container the API's user secrets point at, builds the API and
Web projects, runs the API on `http://localhost:5177` and the Web UI on
`http://localhost:5215`, and stops the API again when the Web UI stops.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev-desktop.ps1
```

In the Claude desktop app the same script is the `vaultflow-web` preview in
`.claude/launch.json`. Then, in a second terminal:

```powershell
dotnet build src/Pos.Client/Pos.Client.csproj -f net10.0-windows10.0.19041.0
./artifacts/bin/Pos.Client/debug_net10.0-windows10.0.19041.0/Pos.Client.exe
```

Visual Studio works too: set **Pos.Client** as the startup project, pick
**Windows Machine**, press F5.

1. **Set up this register.** Keep the head-office address
   `http://localhost:5177`. With a one-time code, enter it and press **Enrol
   this register**. Without one, open **No code yet?**, sign in as `admin`,
   pick a store, name the register and give it an unused 2–6 character code;
   the issued code fills in, then enrol.
2. **Sign in** as a user assigned to the register's store, for example
   `cashier` or `manager` for Store One (any development account signs in with
   the shared password `cash1234`). Signing in downloads the store's
   products and the user's offline permissions; the banner turns **Ready**.
3. The catalogue lists the store's products. The development seed creates a
   selling price for every product and an opening stock balance at the Main
   Warehouse and each store, so a sale completes against real
   availability.

The development accounts are a deliberately short set (one per role), with the
same password `cash1234`:

| Username    | Role               | Scope                                  |
|-------------|--------------------|----------------------------------------|
| `owner`     | Owner              | Business-wide                          |
| `admin`     | Administrator      | Business-wide                          |
| `manager`   | Store Manager      | Store One                              |
| `cashier`   | Cashier            | Store One                              |
| `inventory` | Inventory Staff    | Main Warehouse                         |
| `auditor`   | Auditor            | Business-wide (read-only)              |

To set a register up again, close the app and delete
`%LOCALAPPDATA%\User Name\com.vaultflow.pos\Data\device.db`, then use a new
register code: codes are unique even after a register is revoked.

> **The API must run on the `http` launch profile for the register to connect.**
> `scripts/dev-desktop.ps1` always launches it that way. Running the API with
> the `https` launch profile binds `https://localhost:7256` and makes
> `UseHttpsRedirection` bounce every plain-HTTP call to the HTTPS port; the
> register's connection then fails on the untrusted development certificate
> with *"Head office could not be reached at http://localhost:5177/"*. The
> `/health*` endpoints are exempt from the redirect so liveness probes keep
> working either way.

## Exercise backup and restore locally

```powershell
.\scripts\backup-postgres.ps1 -OutputPath .\artifacts\local-backup.dump
.\scripts\restore-postgres.ps1 -InputPath .\artifacts\local-backup.dump -ConfirmRestore
```

Use a disposable local database for the restore rehearsal. Do not use these
commands against a shared or production database.

## Deferred for now

- GHCR image publishing and deployment-host verification
- Signed Android AAB and Windows MSIX packaging
- External metrics export and production alerting
- Production migration-job separation
- Production TLS/secret rotation and release credential checks
