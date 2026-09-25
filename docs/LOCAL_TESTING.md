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

An API and a Web UI left running by an earlier session are reused instead of
started a second time, so a leftover Web UI is not rebuilt and can serve older
code. Stop it before running the script when the change under test is in the Web
project. The same snippet with `5177` stops the API, which also stops the
`dotnet run` host that started it:

```powershell
Get-NetTCPConnection -LocalPort 5215 -State Listen -ErrorAction SilentlyContinue |
  ForEach-Object { Stop-Process -Id $_.OwningProcess -Force }
```

`Ctrl+C` in the terminal running the script stops the Web UI and the API the
script started itself. Neither route stops the `vaultflow-dev-pg` container; use
`docker stop vaultflow-dev-pg` for that. The container keeps its data in the
`vaultflow-dev-pgdata` volume, so the next run starts where the last one left
off.

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
   products and the offline permissions of the store's staff, and lets the
   register remember this password for offline sign-in; the banner turns
   **Ready**.
3. The catalogue lists the store's products. The development seed creates a
   selling price for every product and an opening stock balance at the Main
   Warehouse and each store, so a sale completes against real
   availability. See [Load the demo business](#load-the-demo-business) for a
   month of history on top.

The development accounts share the password `cash1234`. Store staff work the
registers; the owner, administrator and auditor use the web UI to monitor:

| Username    | Role               | Scope                                  |
|-------------|--------------------|----------------------------------------|
| `owner`     | Owner              | Business-wide                          |
| `admin`     | Administrator      | Business-wide                          |
| `manager`   | Store Manager      | Store One                              |
| `cashier`   | Cashier            | Store One                              |
| `manager2`  | Store Manager      | Store Two                              |
| `cashier2`  | Cashier            | Store Two                              |
| `manager3`  | Store Manager      | Store Three                            |
| `cashier3`  | Cashier            | Store Three                            |
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
> working either way. With head office unreachable the register now works
> offline (see [Work offline at the register](#work-offline-at-the-register)),
> but it can only sign in people who have signed in on it while connected.

## Work offline at the register

The register keeps trading when head office cannot be reached (ADR-0033,
`docs/OFFLINE_SYNC.md` §11). To see it:

1. With the API running, sign in once at the register as each person who
   should be able to work offline, for example `cashier`. A connected sign-in
   is what lets the register check that password later without head office,
   and it downloads the store's products, prices and staff.
2. Stop the API (close `scripts/dev-desktop.ps1`, or stop the `http` profile).
3. Lock the register and sign in again as `cashier` / `cash1234`. The register
   signs in offline: the strip reads **Signed in offline** and the header shows
   **Offline**.
4. Open a shift, ring up a sale and take **cash**; card and e-wallet are
   disabled offline. The receipt prints from the register and is marked as
   recorded offline. The header counts the records waiting to be sent.
5. Start the API again and choose **Reconnect** on the strip (or lock and sign
   in again). The register uploads the shift and the sales, head office replays
   them through the normal sale pipeline, and the count returns to **All sent**.
   Closing the shift works once everything has been delivered.

Offline sign-in is refused, with the reason on screen, for someone who has
never signed in on this register while connected, after five wrong passwords
(for five minutes), and once their cached authority has expired
(`Security:PermissionSnapshotHours`, 72 hours from the last connected sign-in
by anyone at that store).

## Load the demo business

The development seed gives the API a full catalogue (43 products across eight
categories, five suppliers, twelve named customers), per-store restock levels,
and prices and opening stock dated 35 days back. `tools/Pos.DemoData` then
fills that catalogue with a month of activity through the public API, the same
way the store registers and the web UI call it, so every record passes the
normal validation, ledger posting and audit:

```powershell
.\scripts\dev-desktop.ps1                               # API and Web UI running
dotnet run --project tools/Pos.DemoData -- --days 30    # in a second terminal
```

It posts:

- **30 days of trading at all three stores,** through one enrolled register
  per store: `W02` (a second Windows counter at Store One), `W03` (Store Two)
  and `A01` (an Android phone till at Store Three). The store's cashier signs
  in at the register, the register numbers its own documents as it does
  offline, and the store manager voids and refunds. Each day has a shift with
  cash, card and e-wallet sales, customer-attached sales, the odd void and
  return, and a cash count, some with small variances. The real desktop
  register `W01` is never used, so its own numbering is unaffected.
- **Back-office work at every stage:**
  - Purchase orders: received in full, received short with damage, awaiting
    approval, and a draft.
  - Transfers: received, in transit, approved, awaiting approval, and a draft.
  - Stock counts: one approved, one in progress.
  - Stock adjustments: approved, awaiting approval, and a draft.
  - Payment receipts and a quarantine incident.

Running it again is safe. Store-days that already have sales are skipped, so
an interrupted run can simply be restarted, and the back-office activity is
posted once unless `--force` is given. Back up first if the database holds
anything you want to keep:

```powershell
docker exec vaultflow-dev-pg pg_dump -U pos_migrator -d vaultflow --format=custom > backups\before-demo.dump
```

Selling happens only at the store registers: they work offline and sync to
head office. The web UI is read-only for store records. The owner monitors each
store there: **Store performance** (takings, sales, transactions, payment mix,
voids, refunds and best sellers per store over a period) and **Store
inventory** (each location's stock, with the items running lowest and the ones
most abundant against their target), alongside the sales ledger, payment
receipts, counts, adjustments, transfers and quarantine, which are all view
only. Administration (locations, people, devices, prices) stays editable.
A register keeps an offline copy of its store's data that refreshes when a
cashier signs in, or with **Re-download** on the signed-in view, and its sales
appear in the web **Sales ledger** once they sync.

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
