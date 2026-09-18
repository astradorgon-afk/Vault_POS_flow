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
