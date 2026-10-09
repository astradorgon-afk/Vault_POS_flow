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

## Test web camera scanning on Android

For the simplest phone test, keep the local API and Web UI running, start the
Docker ngrok tunnel, and open the printed HTTPS URL on the phone:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/start-phone-ngrok.ps1
```

On first use, the script asks for an ngrok authtoken and saves it in the ignored
`.secrets/ngrok.env` file. The token is never passed on the Docker command line.
The phone can use any internet connection; it needs no local CA certificate or
Windows inbound firewall rule. Continue past ngrok's one-time browser notice,
sign in, and tap **Scan with camera** on the purchase order or receiving page.
The tunnel exposes this development site to the internet while it runs; stop it
after testing with `docker stop vaultflow-phone-ngrok`.

The LAN-only Caddy option below remains available if a public tunnel is not
suitable:

Camera access needs a trusted HTTPS page when a phone uses this computer's LAN
address. With the API and Web UI running, connect the phone to the same Wi-Fi
and run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/start-phone-https.ps1
```

The script prints the current LAN address. On the phone, download the public
certificate from `http://<printed-ip>:8445/phone-ca.crt`. In Android Settings,
search for **Install a certificate**, choose **CA certificate**, and select the
downloaded file. Then open `https://<printed-ip>:8444` in Chrome, sign in, and
use **Scan with camera**. The private signing key stays in the local Docker
volume; only the public certificate is downloadable. You can transfer the
`.secrets/vaultflow-phone-ca.crt` file by USB instead of downloading it over
the LAN.

If either address times out on the phone, run the relevant `New-NetFirewallRule`
commands printed by the script in an Administrator PowerShell, then retry on
the same Wi-Fi. The script detects whether Windows labels that Wi-Fi **Public**
or **Private** and restricts the rules to the local subnet. The HTTPS app uses
port 8444 because port 8443 timed out on the test Android phone while HTTP on
8444 worked. Do not continue past a browser certificate warning: the phone
must trust the installed CA before camera access can work. Remove the
**VaultFlow local/Caddy** CA from Android's user-installed certificates after
testing; a trusted CA can validate other sites too. Stop the proxy with
`docker stop vaultflow-phone-https` when finished.

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
   `cashier` or `manager` for Legazpi Village (any development account signs
   in with the shared password `cash1234`). Signing in downloads the store's
   products and the user's offline permissions; the banner turns **Ready**.
3. The till groups the store's products by category (Rice & Grains, Beverages,
   Personal Care, …); search by name, SKU or barcode reaches all of them. The
   development seed creates a selling price for every product and an opening
   stock balance at the distribution centre and each store, so a sale
   completes against real availability. See
   [Load the demo business](#load-the-demo-business) for a month of history on
   top.

The development accounts share the password `cash1234`. Store staff work the
registers; the owner, administrator and auditor use the web UI to monitor:

| Username    | Name                | Role                   | Scope                                         |
|-------------|---------------------|------------------------|-----------------------------------------------|
| `owner`     | Ramon Dela Cruz     | Owner                  | Business-wide                                 |
| `admin`     | Patricia Lim        | Administrator          | Business-wide                                 |
| `auditor`   | Teresa Gonzales     | Auditor                | Business-wide (read-only)                     |
| `warehouse` | Ernesto Villanueva  | Main Inventory Manager | MAIN — Valenzuela Distribution Center         |
| `inventory` | Jonathan Cruz       | Inventory Staff        | MAIN — Valenzuela Distribution Center         |
| `manager`   | Carmela Reyes       | Store Manager          | STORE01 — Legazpi Village, Makati             |
| `cashier`   | Joy Mendoza         | Cashier                | STORE01 — Legazpi Village, Makati             |
| `cashier1b` | Mark Anthony Santos | Cashier                | STORE01 — Legazpi Village, Makati             |
| `manager2`  | Dennis Aquino       | Store Manager          | STORE02 — Tomas Morato, Quezon City           |
| `cashier2`  | Kristine Bautista   | Cashier                | STORE02 — Tomas Morato, Quezon City           |
| `cashier2b` | Rowena Garcia       | Cashier                | STORE02 — Tomas Morato, Quezon City           |
| `manager3`  | Lourdes Navarro     | Store Manager          | STORE03 — Kapitolyo, Pasig                    |
| `cashier3`  | Paolo Ramos         | Cashier                | STORE03 — Kapitolyo, Pasig                    |
| `cashier3b` | Janine Torres       | Cashier                | STORE03 — Kapitolyo, Pasig                    |

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

## Load the demo business

The development seed builds **Suki Mart**, a neighbourhood grocery chain with a
distribution centre in Valenzuela and three branches (Legazpi Village in
Makati, Tomas Morato in Quezon City, Kapitolyo in Pasig):

- **749 products** in 18 categories from 59 brands and 12 suppliers, each with a
  valid EAN-13 barcode, a cost and a selling price at local market levels, and
  a few price rises part-way through the month. The catalogue is generated
  deterministically (`DevelopmentCatalogue`), so every machine seeds the same.
- **Opening stock sized for a month of sales,** with restock levels per store
  set from each product's sales rate, so after the demo month most shelves are
  healthy, some are low and a few have sold out.
- **67 customers** (regulars and businesses that buy on account), the staff
  accounts above, and a receipt header and return-policy footer per branch.

`tools/Pos.DemoData` then trades a month through the public API, the same way
the store registers and the web UI call it, so every record passes the normal
validation, ledger posting and audit:

```powershell
.\scripts\dev-desktop.ps1                               # API and Web UI running
dotnet run --project tools/Pos.DemoData -- --days 30    # in a second terminal
```

It takes about five minutes and posts:

- **About 25,000 sales over 31 days** (roughly 800 checkouts and ₱300,000 of
  sales a day across the chain), through two enrolled registers per store:
  `W02`/`W04` at Legazpi Village, `W03`/`W05` at Tomas Morato and the Android
  phone tills `A01`/`A02` at Kapitolyo. The morning cashier opens the first
  counter before 7:00 and counts the drawer mid-afternoon; the afternoon
  cashier runs the second until after 22:00. Shoppers arrive on a realistic
  daily curve (a lunchtime peak and a bigger evening one), weekends and
  paydays are busier, baskets range from a single item to a weekly shop, and
  each product sells at the rate its opening stock was planned for. Payment
  is mostly cash, then e-wallet, then card; about one shopper in 25 gets the
  senior citizen/PWD 20% discount, authorised by the store manager. About one
  sale in 400 is voided and one in 350 comes back within the 7-day return
  window and is refunded the way it was paid. Registers number their own documents as they do
  offline. The real desktop register `W01` is never used, so its numbering is
  unaffected.
- **Back-office work at every stage:**
  - Purchase orders to the distribution centre, sized to two weeks of the
    chain's sales: received in full, received short with a damaged case, sent,
    awaiting approval, and a draft.
  - Restock transfers for what each store actually ran low on: received, in
    transit, approved, awaiting approval, and a draft.
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

### Start over with fresh demo data

The seeder only adds what is missing, so a database created before this demo
business keeps its old products alongside the new ones (the API logs a
warning when it finds the old catalogue). To start clean, remove the
development database and let the script recreate it; the API migrates and
seeds it on start:

```powershell
docker rm -f vaultflow-dev-pg
docker volume rm vaultflow-dev-pgdata
.\scripts\dev-desktop.ps1
dotnet run --project tools/Pos.DemoData -- --days 30    # in a second terminal
```

The desktop register's enrolment belonged to the old database, so set it up
again as described under [Run the desktop register](#run-the-desktop-register-windows)
(delete its `device.db` and enrol with a new code).

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

## Install and test the offline browser app on a phone

With the API running on port 5177:

```powershell
.\scripts\start-phone-pwa.ps1
```

This publishes the web host in Release, starts it on local port 5321, and prints an ngrok HTTPS URL ending in `/login`. Open that link in Android Chrome and click through ngrok's one-time notice. Sign in online with a work location; enter the one-time installation enrollment code if this browser has not been enrolled. Wait until the page says **Offline access is ready on this device** before leaving it. Enable airplane mode, reopen the same `/login` URL, sign in with the account password, and test purchasing, receiving, and downloaded inventory. Reconnect and review each queued operation's confirmation.

The script saves host/tunnel process IDs and the URL in ignored `artifacts/phone-pwa/pilot.json`; its output includes the stop command. Use `-SkipPublish` to reuse an already published build. Use `-ReuseTunnel` to publish a replacement and restart only the web host while keeping the existing phone address. Keep the tunnel running during the pilot. Free ngrok tunnel URLs change on restart, and device enrollment/data are origin-bound: use a permanent HTTPS domain before real deployment. Do not clear the old origin's storage while it holds pending work.

To issue the code, open the website's **Administration → Devices**, register **Offline browser app (PWA)** for the work location, and enter its one-time enrollment code in the installed app. A browser lane is a different device type and does not provide this offline enrollment.

Do not use a `dotnet run` development worker to verify installation/offline behavior, and do not rely on a temporary free tunnel address for real deployment. A shortcut alone does not prove offline readiness. The existing server-rendered website still needs a connection; the browser-run PWA currently provides purchasing, receiving and downloaded inventory. Cash sales and shifts remain outside its scope.

## Deferred for now

- GHCR image publishing and deployment-host verification
- Signed Android AAB and Windows MSIX packaging
- External metrics export and production alerting
- Production migration-job separation
- Production TLS/secret rotation and release credential checks
