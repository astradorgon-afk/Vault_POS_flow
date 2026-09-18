# Deployment

---

## 1. Topology

```
                Internet
                   | 443
            +------v-------+
            | reverse proxy |  Caddy or nginx: TLS, HSTS, security headers,
            +------+--------+  request size limits, IP allow-list for /health/ready
        +----------+----------+
   +----v----+ +---v----+
   | pos-api | |pos-web |          (scale pos-api horizontally; add redis first)
   +----+----+ +---+----+
        +-----+----+
        +-----v------+   +-----------------+
        | postgres   |   | pos-migrator    |  one-shot job, runs before api
        +------------+   +-----------------+
```

Containers run as non-root with a read-only root filesystem and a writable
`/tmp` and data-protection volume only.

---

## 2. Compose services

| Service | Image | Notes |
|---|---|---|
| `postgres` | `postgres:17-alpine` | named volume, healthcheck, tuned `shared_buffers`/`work_mem`; `01-roles.sql` creates `pos_app`/`pos_readonly` on first start |
| `migrator` | built from `build/docker/Dockerfile.migrator` | an EF migrations bundle run as `pos_migrator`, exits 0 |
| `grants` | `postgres:17-alpine` | runs `02-grants.sql` as `pos_migrator` after the migrator, exits 0; safe to repeat |
| `api` | `build/docker/Dockerfile.api` | depends on `grants` completing; connects as `pos_app`; reads the token-signing key from the `jwt_signing_key` Docker secret |
| `pos-web` | `build/docker/Dockerfile.web` | Blazor Web App |
| `proxy` | `caddy:2-alpine` | TLS (ACME in production, local CA in dev) |
| `redis` *(optional)* | `redis:7-alpine` | SignalR backplane + permission cache when `pos-api` is scaled |

Dev overlay adds Adminer and Seq; neither is present in the production compose file.

---

## 3. Configuration

All configuration is environment-variable driven, validated at startup with
`ValidateOnStart`. The API refuses to boot in Production if any required secret
is missing or equals a known development default.

| Variable | Purpose |
|---|---|
| `ConnectionStrings__Postgres` | `pos_app` role connection string |
| `ConnectionStrings__PostgresMigrator` | `pos_migrator` role, migrator only |
| `Jwt__Issuer`, `Jwt__Audience` | token validation parameters |
| `Jwt__SigningKeyPem`, `Jwt__PreviousSigningKeyPem` | RS256 key ring (rotation with overlap) |
| `Jwt__AccessTokenMinutes` (10), `Jwt__RefreshTokenDays` (30) | lifetimes |
| `DataProtection__KeyRingPath` | mounted volume for the key ring |
| `Security__RequireTwoFactorForAdmins` | default `true` |
| `Identity__PasswordHashIterations` | default 600000 |
| `Cors__AllowedOrigins` | `Pos.Web` origin only |
| `Database__ApplyMigrationsOnStartup` | `true` only in Development |
| `Serilog__MinimumLevel`, `Serilog__WriteTo__*` | logging sinks |
| `Organization__TimeZoneId` | default `Asia/Manila` |
| `Organization__CurrencyCode` | default `PHP` |
| `Sync__FeedRetentionDays` (30), `Sync__MaxBatchEvents` (100) | sync tuning |

Secrets come from Docker secrets or the platform's secret store; they are never
baked into images and never committed. `scripts/check-secrets.ps1` runs in CI and
as a pre-commit hook.

The API adds a key-per-file configuration source over `/run/secrets`: a secret
mounted with the target name `Jwt__SigningKeyPem` supplies `Jwt:SigningKeyPem`,
and any other setting can be supplied the same way. In development,
`scripts/init-dev-secrets.ps1` (Windows PowerShell 5.1 or PowerShell 7) writes
the signing key to `.secrets/jwt-signing-key.pem`, which compose mounts, and
creates `.env` with the database passwords if it does not exist. Then:

```bash
docker compose up -d --build
```

The stack is served by Caddy at `https://localhost` with a locally trusted
certificate; in Development the seeded staff accounts use `DevVaultFlow!2026`.
Set `SITE_ADDRESS` in `.env` to serve other names or addresses too (for example
`SITE_ADDRESS=localhost, pos.store.lan`), so tills on the store network reach the
same certificate-backed site.

---

## 4. TLS and proxy

- Production: ACME certificates, automatic renewal, TLS 1.2 minimum.
- Development: a locally trusted certificate; MAUI clients trust the dev CA only
  in Debug builds, and certificate pinning is active in Release builds.
- The proxy sets HSTS, CSP, `X-Content-Type-Options`, `Referrer-Policy`,
  `Permissions-Policy`, COOP/CORP (SECURITY.md §4), and forwards
  `X-Forwarded-For`/`Proto`, which the API consumes via `ForwardedHeaders` with
  `KnownProxies` restricted to the proxy's address.

---

## 5. Database operations

**Backups**

- Nightly `pg_basebackup` plus continuous WAL archiving to off-host storage.
- Backups are encrypted at rest with a key held separately from the database
  credentials.
- Retention: 35 daily, 12 monthly, 7 yearly.
- `scripts/backup.ps1` / `scripts/restore.ps1`; a restore drill into a scratch
  container is part of the monthly operations checklist and its result is
  recorded — an untested backup is not a backup.

**Migrations**

- `pos-migrator` runs to completion before any API container starts.
- Forward-only. Rollback is restore-plus-replay, because the ledger cannot be
  un-inserted (ADR-0014).
- No partition maintenance: the ledger and audit log are not partitioned in v1
  (ADR-0030).

**Roles** — created by the migrator, not by the app: `pos_migrator` (owner),
`pos_app` (least privilege, `SELECT, INSERT` only on ledger/audit/processed_event),
`pos_readonly` (reporting views).

---

## 6. Client packaging

| Target | Artefact | Notes |
|---|---|---|
| Windows POS | MSIX, signed | sideload or Store; auto-update channel configurable |
| Android POS / terminal | signed AAB + APK | minSdk 26; APK for controlled distribution |

Client configuration (API base URL, environment, update channel) ships in
`appsettings.client.json` and can be overridden at enrolment. Device enrolment
uses a one-time code issued from the admin UI; the device generates its key pair
locally and never transmits a private key.

---

## 7. Operations runbook

| Situation | Action |
|---|---|
| Ledger drift alert | Do **not** auto-correct. Inspect `integrity_incident`, compare ledger sum vs balance for the bucket, rebuild balances if the ledger is correct, otherwise escalate — the ledger is the truth |
| Device offline > 24 h | Check `sync/devices/health`; contact the store; local sales are safe and queued |
| Sync failures accumulating | `GET /sync/failures`, group by error code; fix root cause; retry individually |
| Emergency transfers pending review | Review queue must be cleared daily; escalated after 48 h |
| Suspected device compromise | Revoke the device (`device.manage`); it wipes caches on next contact and preserves the outbox |
| Suspected credential compromise | Disable the user; refresh-token family revoked; audit trail reviewed |
| Database restore | Restore to a scratch instance first, verify the ledger global sum is zero, then cut over |

---

## 8. CI

`.github/workflows/ci.yml`:

1. restore, build with warnings-as-errors
2. unit tests (domain, application)
3. integration tests with Testcontainers PostgreSQL
4. sync and security test suites
5. architecture tests
6. coverage floors (`scripts/check-coverage.ps1`)
7. secret scan
8. publish API/Web container images on `main`

`verify-migrations` restores before it runs `dotnet ef`: the tool builds the
project it is pointed at, and without an assets file it fails with `NETSDK1004`
before it reads the model, which is not the same answer as "the model has
pending changes".

The MAUI client is built on Windows runners for the Windows target and Linux
runners for Android. Client builds are gated on the shared projects compiling and
the shared UI tests passing.

How that is wired (since C29c):

- `build-and-test` runs on Ubuntu without the MAUI workloads, so its first step
  removes `Pos.Client` from that checkout's copy of `VaultFlow.slnx`. Everything
  else in the solution, including projects added later, is restored, built and
  tested as before.
- `build-client-android` (Ubuntu) and `build-client-windows` (Windows) run only
  after `build-and-test` passes, and build the client in Release, which runs the
  Android trimmer under warnings-as-errors. The Android job installs .NET under
  the runner's temp directory so the workload install needs no elevated rights,
  and tops up the Android SDK with `InstallAndroidDependencies`.
- Both install their workloads from workload set `10.0.301`, the set matching
  the SDK in `global.json`. When `global.json` moves to another SDK band, move
  the workload set with it (`dotnet workload search version` lists them).
- The test step runs under `coverlet.runsettings`, which keeps EF's migration
  designer files, model snapshots and generated sources out of the measurement —
  they are 100,000 of the 160,000 lines otherwise counted, and none of them runs
  outside a migration. `scripts/check-coverage.ps1` then merges the per-project
  Cobertura reports and fails the job under its floors. The floors are a ratchet
  a couple of points below what the suite covers today: raise them as coverage
  rises, and say why in the commit message if one ever has to come down.
