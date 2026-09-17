# Security Model

---

## 1. Threat model

| # | Threat | Primary control |
|---|---|---|
| T1 | Employee inflates or hides stock by editing quantities | No writable stock field; immutable double-entry ledger; DB triggers; restricted DB role |
| T2 | Store receives unauthorized goods and sells them | Unknown barcode → quarantine incident; unauthorized delivery → quarantine; release requires HQ permission |
| T3 | Store manager moves stock between stores secretly | Store-to-store transfers require central approval or a signed pre-approval token; emergency transfers are flagged and reviewed |
| T4 | Cashier voids/refunds to pocket cash | `sale.void`/`sale.refund` gated, audited, same-shift-only, shift cash variance reported |
| T5 | Offline mode used to bypass approvals | Offline-capable permission subset excludes all approval permissions; approvals become `PendingCentralReview` |
| T6 | Replayed or duplicated sync uploads inflate sales/stock | Idempotency by `event_id` with stored result, per-device monotonic sequence, payload hash |
| T7 | Stolen device used to transact | Device enrolment, device-bound tokens, revocation with cache wipe, short access-token lifetime |
| T8 | Credential stuffing / brute force | Login throttling, lockout, rate limits, breached-password rejection |
| T9 | Token theft / session hijack | Short-lived access tokens, rotating refresh tokens with reuse detection, HTTPS + HSTS |
| T10 | SQL injection / data exfiltration | EF Core parameterisation, no dynamic SQL from user input, least-privilege DB roles, read-only reporting role |
| T11 | XSS in the admin dashboard | Razor auto-encoding, strict CSP, no `MarkupString` on user data, sanitised free text |
| T12 | Insider tampering with history | Append-only tables + triggers + `SELECT, INSERT`-only grants; audit log is itself immutable |
| T13 | Card data compromise | No PAN/CVV ever stored; tokenised external provider only |
| T14 | Backup theft | Encrypted backups, separate credentials, restore drills |
| T15 | Privilege escalation via role edits | `role.manage` restricted to Owner/Administrator, every change audited with before/after |

---

## 2. Authentication

### 2.1 Users

- **ASP.NET Core Identity** with a customised `AppUser`/`AppRole` (Guid keys).
- Password hashing: Identity v3 (PBKDF2-HMAC-SHA512), iteration count raised to
  **600,000**, 128-bit salt. The hasher is configured explicitly, not defaulted,
  and the iteration count is a setting so it can be raised over time; Identity's
  embedded format version lets old hashes verify and be re-hashed on next login.
- Password policy: minimum 12 characters, no composition rules (NIST SP 800-63B),
  rejected against a local list of the top 10k breached passwords plus
  organization-specific terms.
- Optional TOTP second factor; **required** for any account holding `user.manage`
  or `role.manage` when `Security:RequireTwoFactorForAdmins` is on (the production
  default; off in Development and the test hosts). Decided by permissions, never by
  role name. Such an account is refused sign-in (`auth.two_factor_enrolment_required`)
  until it enrols through the password-authenticated, throttled
  `auth/two-factor/setup` and `/enable` calls, which only work before two-factor is
  on. Enrolment returns eight one-time recovery codes accepted at sign-in in place
  of the authenticator code. A lost authenticator is reset by another
  administrator, which rotates the key and ends the account's sessions.
- Lockout: 5 failed attempts → 15-minute lockout, exponential thereafter.
  Throttling is applied per account **and** per IP so a lockout cannot be used to
  deny service to a cashier.
- `SecurityStamp` changes on password change, role change, or disable, which
  invalidates all issued tokens at the next validation.

### 2.2 POS cashier sign-in

Cashiers sign in on a shared device with **employee code + PIN**, which is a
credential of its own:
- PIN is 6 digits minimum, hashed with the same Identity hasher, per-user salt,
  never stored or logged in plaintext, and rate-limited to 5 attempts per 5
  minutes per device.
- A PIN is only valid on a device whose `LocationId` matches one of the user's
  assigned locations, and only while the device is `Active`.
- PIN sign-in issues the same token pair as password sign-in, but with a shorter
  refresh lifetime (12 h vs 30 d) and only the offline-capable permission subset.

### 2.3 Tokens

| Token | Lifetime | Storage | Notes |
|---|---|---|---|
| Access (JWT) | 10 min | memory only | claims: `sub`, `device_id`, `loc`, `sstamp`, `policy_ver`, `jti` |
| Refresh | 30 d (12 h for PIN) | hashed (SHA-256) in DB; client keeps it in the platform secure store | single-use, rotated on every refresh |
| Device enrolment code | 15 min, single use | DB | issued by `device.manage`, redeemed once |
| Pre-approval token | ≤ 7 d | DB + signed copy on device | scoped, value-capped, single-use |

Access tokens deliberately **do not carry the permission list** — it is large and
mutable. They carry `sstamp` and `policy_ver`; the API resolves permissions from
a cache keyed by `(userId, policyVersion)` that is invalidated on any
role/permission change. This makes permission revocation effective immediately
rather than at token expiry.

**Refresh token rotation with reuse detection**: each refresh issues a new token
and marks the old one `replaced_by`. Presenting an already-replaced token means
the token was stolen: the entire token family for that user+device is revoked,
a `Critical` security notification is raised, and the device is suspended
pending review.

JWT validation: `ValidateIssuer`, `ValidateAudience`, `ValidateLifetime`,
`ValidateIssuerSigningKey` all on; `ClockSkew` reduced to 30 seconds; algorithm
pinned to RS256 with key rotation via a JWKS-style key ring (two active keys,
30-day overlap). No `HS256` fallback, no `none`.

### 2.4 Devices

Enrolment: an admin with `device.manage` issues a one-time code; the device posts
it with its platform details and a generated key pair; the server stores the
public key thumbprint and returns the `DeviceId` + first token pair.
`Suspended`/`Revoked` devices are refused at token refresh and at every sync call.

---

## 3. Authorization

See [PERMISSIONS.md](PERMISSIONS.md). Implementation:

```csharp
[HttpPost("{id:guid}/approve")]
[RequirePermission(Permissions.Transfer.Approve, Scope = ScopeSource.RouteLocation)]
public async Task<IActionResult> Approve(Guid id, ...)
```

- `RequirePermissionAttribute` → `IAuthorizationRequirement` → handler that
  resolves the user's effective permissions and the requested location scope.
- A dynamic `IAuthorizationPolicyProvider` materialises a policy per permission
  code so policies need not be registered one by one.
- `AuthorizationBehaviour` repeats the check in the application pipeline, so a
  command dispatched from the Blazor server, a background worker, or the sync
  processor is checked identically. **Defence in depth, not duplication by accident.**
- `Pos.Architecture.Tests` fails the build for any public API endpoint lacking
  either `[RequirePermission]` or an explicit `[AllowAnonymousDocumented]`.

---

## 4. Transport and headers

- HTTPS only. HTTP redirects to HTTPS; HSTS `max-age=63072000; includeSubDomains; preload`.
- TLS terminated at the reverse proxy, TLS 1.2 minimum, modern cipher suite list.
- Client certificate pinning (issuer pinning) in the MAUI HTTP handler.
- Security headers on every response:
  ```
  Content-Security-Policy: default-src 'self'; script-src 'self' 'wasm-unsafe-eval';
      style-src 'self'; img-src 'self' data: blob:; connect-src 'self' wss:;
      frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'
  X-Content-Type-Options: nosniff
  Referrer-Policy: no-referrer
  Cross-Origin-Opener-Policy: same-origin
  Cross-Origin-Resource-Policy: same-origin
  Permissions-Policy: camera=(self), geolocation=(), microphone=(), payment=()
  ```
  `camera=(self)` is required for browser-based barcode scanning in `Pos.Web`.
- CORS: closed by default; only the configured `Pos.Web` origin is allowed, with
  credentials, for the specific methods used.
- CSRF: `Pos.Web` uses cookie auth and enables antiforgery on every state-changing
  form and Blazor callback. The API uses bearer tokens and does not accept cookie
  authentication, which removes the CSRF surface there entirely.

---

## 5. Input handling

- FluentValidation on every command; validation failures return RFC 9457
  `ProblemDetails` with field-level errors and no internal detail.
- Barcodes, SKUs and document numbers are validated against strict regexes at the
  value-object boundary, so malformed input cannot reach persistence.
- All queries are parameterised by EF Core. `FromSqlRaw` is banned by an
  architecture test; `FromSqlInterpolated` is permitted only in the reporting
  module and only with non-user-controlled shape.
- Free-text fields are length-capped, stored as-is, and **HTML-encoded at render**.
  No stored HTML, no `MarkupString` over user data.
- Uploaded images: extension + magic-byte + dimension checks, re-encoded server
  side, stored outside the web root, served through a permission-checked endpoint,
  `Content-Disposition: attachment` for anything not an image.
- Request body size limits and a hard cap on sync batch size.

---

## 6. Rate limiting

Using the built-in rate limiter:

| Policy | Limit |
|---|---|
| `auth-login` | 10 / 5 min per IP, 5 / 5 min per account |
| `auth-refresh` | 30 / hour per device |
| `sync-push` | 60 / min per device |
| `sync-pull` | 600 / hour per device |
| `api-default` | 300 / min per user |
| `report-heavy` | 10 / min per user |

Exceeding a limit returns `429` with `Retry-After`. Limit breaches on auth
endpoints are audited.

---

## 7. Secrets and configuration

- **No secrets in source control.** `appsettings.json` contains structure and
  non-secret defaults only; a `.gitignore` and a pre-commit secret scan enforce it.
- Development: .NET user-secrets. Production: environment variables injected from
  the orchestrator's secret store (Docker secrets / cloud KMS-backed store).
- Required-secret validation at startup: the API refuses to start in Production
  if the signing key, connection string, or data-protection key ring is missing
  or is a known development default.
- ASP.NET Data Protection keys persisted to a mounted volume and encrypted at
  rest, so cookies and tokens survive container restarts without being shareable.
- Connection strings use a least-privilege role (`pos_app`), never the owner.
- Client-side: API base URL and enrolment code are configuration; the refresh
  token and SQLite encryption key live in the platform secure store
  (Windows Credential Locker / DPAPI, Android Keystore). The database key is
  256 random bits applied as a SQLCipher raw key: key stretching protects
  guessable passphrases and adds nothing to a random key, while costing
  hundreds of milliseconds on every connection open.

---

## 8. Logging and error handling

- Serilog with a `SensitiveDataScrubber` enricher (on the bootstrap logger and
  the host logger) that replaces with `***` any property whose name ends in a
  secret marker — `password`, `secret`, `token`, `apikey`, `privatekey`,
  `signingkeypem`, `connectionstring`, `authorization`, `cookie`, `pin`,
  `pinhash`, `twofactorcode`, `recoverycode(s)`, `enrolmentcode`, `sharedkey`,
  `authenticatorkey` — at the top level, inside destructured objects and inside
  dictionaries. Names like `PreApprovalTokenId` and `ErrorCode` are left alone.
  The pipeline itself logs message names, error codes and identifiers, never
  request bodies; the scrubber guards against a future log line that does.
  *(Card fields — `cardNumber`, `cvv`, `pan` — join the list with card payments in
  Phase 11.)*
- Production error responses are RFC 9457 `ProblemDetails` with a stable
  `errorCode`, a safe message, and the `correlationId`. **No stack traces, no
  exception types, no SQL.** The full exception goes to the log, correlated by id.
- Developer exception page is enabled only in Development.
- Audit logging is separate from diagnostic logging and is durable, immutable,
  and queryable by `audit.view` holders only.

---

## 9. Payments

- No PAN, no CVV, no magnetic-stripe data, no PIN blocks are stored, logged, or
  transmitted through this system — ever.
- Card and e-wallet payments are delegated to a certified external provider. The
  `payment` row stores `Provider`, `ProviderTransactionRef`, `PaymentToken`,
  `MaskedLast4`, `Amount`, `Status`, and nothing else.
- Refunds are executed by reference to the provider transaction, never by
  re-entering card details.
- Because card data never enters the environment, PCI DSS scope is limited to
  SAQ-A style controls; this is a deliberate architectural choice.

---

## 10. Audit log

Written inside the same transaction as the change it describes. Immutable
(triggers + `SELECT, INSERT` grant). Covers, at minimum:

login, logout, failed authentication, lockout, password change, PIN change,
product create/modify, barcode modify, price change, cost change, PO create,
purchase approval, goods receipt, receiving discrepancy, transfer create /
approve / reject / dispatch / receive / discrepancy / reconcile, direct
supplier-to-store delivery, quarantine create / release / reject, stock
adjustment create / approve, inventory count submit / approve, sale void,
refund, discount override, price override, expired-batch override, user create /
disable, role change, permission change, device registration / suspension /
revocation, sync conflict, sync rejection, emergency transfer, negative-stock
attempt, balance rebuild, settings change.

Each entry: user, role snapshot, device, location, IP, user agent, action,
entity type/id, previous value, new value, reason, reference document,
UTC timestamp, correlation id.

Retention: 7 years, older entries moved to cold storage but never deleted while
the business operates. The table is not partitioned yet; the archiving work that
first needs it (2033) brings monthly partitions (ADR-0030).

---

## 11. Security test suite (`Pos.Security.Tests`)

| Test | Asserts |
|---|---|
| `EveryEndpoint_RequiresPermission` | no unattributed endpoint |
| `Anonymous_CannotAccessProtectedEndpoints` | 401 across the surface |
| `Cashier_CannotApproveTransfer` | 403 |
| `StoreManager_CannotAdjustOtherLocation` | 403 on cross-location scope |
| `StoreManager_CannotCreateProduct` | 403 |
| `InventoryStaff_CannotApproveOwnAdjustment` | 403 self-approval |
| `Auditor_IsReadOnly` | every mutating endpoint 403 |
| `RevokedDevice_CannotSync` | 403 + wipe directive |
| `DisabledUser_TokenRejected` | security stamp invalidation |
| `ExpiredAccessToken_Rejected` | 401 |
| `ReusedRefreshToken_RevokesFamily` | reuse detection |
| `ReplayedSyncBatch_ProducesDuplicatesOnly` | no double post |
| `TamperedJwtSignature_Rejected` | signature validation |
| `AlgNone_Rejected` | algorithm confusion |
| `LoginThrottling_LocksAfterNAttempts` | lockout policy |
| `ErrorResponses_ContainNoStackTrace` | production error shape |
| `SecurityHeaders_Present` | header assertions |
| `PasswordHash_UsesConfiguredIterations` | hashing strength |
| `NoSecretsInRepository` | scan of tracked files |
