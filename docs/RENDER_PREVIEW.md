# Render trial deployment

`render.yaml` defines a temporary trial with two free Docker web services and one free Postgres database in Singapore. The web service serves the existing website and `/offline/` PWA. The API is a separate public service because Render free web services cannot receive private-network requests. The web app calls that API from its server; browser API tokens stay behind the web gateway.

## Before creating the Blueprint

1. Connect the GitHub repository containing these changes to Render and deploy the branch with `render.yaml`.
2. Generate a new RSA private key for `Jwt__SigningKeyPem` (for example, `openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072`). Store it in a password manager. Paste the complete PEM, including its header and footer, into Render's initial Blueprint secret prompt. Never commit it.
3. Choose a unique, long `BootstrapOwner__Password` in the second secret prompt. The trial creates username `owner` only when the database has no users. Store this password securely. Do not use a development account password.
4. Create the Blueprint in the Render dashboard from the repository. Wait for Postgres and `vaultflow-preview-api` to become healthy, then check the `vaultflow-preview-web` URL. Use that stable `https://...onrender.com/offline/` address for a fresh PWA installation and enrollment.

The API keeps administrator two-factor protection enabled. Before the first owner sign-in, call `POST https://vaultflow-preview-api.onrender.com/api/v1/auth/two-factor/setup` with JSON `{"userName":"owner","password":"<owner password>"}`. Add the returned authenticator key to an authenticator app, then call `/api/v1/auth/two-factor/enable` with the same username and password plus its current six-digit `code`. Store the recovery codes securely. The API URL shown here is the default generated from the Blueprint name; use the actual Render service URL if it differs. These setup calls contain the password, so use HTTPS and do not put it in a shell history or a shared log.

## Trial constraints

- Render's free web services sleep after 15 minutes without traffic. A wake-up can take about a minute. The API and web app can wake separately, so the first request may take longer. Installed PWA files and previously downloaded employee data remain available offline in the browser.
- Free Postgres expires after 30 days and has no backups. Treat this as a trial with fresh data, not a store of record. Upgrade or export the database before it expires.
- The API uses `Database__AllowProductionStartupMigration=true` only for this single-instance trial because free services cannot run a pre-deploy command. For a durable deployment, turn this off, use a migration job, split schema and application database roles, and use paid services.
- Render restarts and sleep cycles clear in-memory web gateway sessions. Offline device data stays encrypted in browser storage, but staff may have to sign in online again before pending work can sync.
- A new Render origin cannot read profiles, drafts, or pending operations stored under an old tunnel origin. Re-enroll the PWA and reconcile any pending work before leaving the old installation.

The published app should pass `GET /health/live` on the API and return the PWA manifest at `/offline/manifest.webmanifest` on the web service. Verify registration, sign-in, employee preparation, downloaded data, offline reopening, and reconnect before using it for live work.
