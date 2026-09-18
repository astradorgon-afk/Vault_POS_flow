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
   `cashier1` or `store1.mgr` for Store One. Signing in downloads the store's
   products and the user's offline permissions; the banner turns **Ready**.
3. The catalogue lists the store's products. The development seed creates no
   prices, so each shows **No price** until a manager schedules one.

To set a register up again, close the app and delete
`%LOCALAPPDATA%\User Name\com.vaultflow.pos\Data\device.db`, then use a new
register code: codes are unique even after a register is revoked.

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
