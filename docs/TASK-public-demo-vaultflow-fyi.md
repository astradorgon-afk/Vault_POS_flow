# Task: public demo at vaultflow.fyi — what is left

Branch: `deploy/vaultflow-fyi` (deployment only; development continues on
`claude/desktop-offline-web-design-esd5lo`). Last updated 2026-10-19.

## Where things stand

**Live and working:** https://vaultflow.fyi serves the sign-in page through a
Cloudflare tunnel from the home server.

- **Server:** the owner's home server (Ubuntu, Docker), reached by SSH over a
  private network. A cloud session cannot reach it; every server step below is
  run by the owner from an SSH window.
- **Checkout:** `~/vaultflow` is a `git archive` copy of this branch, not a git
  clone. `DEPLOYED_FROM` there names the branch and time.
- **Secrets:** `~/vaultflow/.env` and `~/vaultflow/.secrets/` exist only on the
  server and are never committed. `.env` holds the database passwords,
  `BOOTSTRAP_OWNER_PASSWORD`, `TUNNEL_TOKEN`, `DEMO_ACCOUNT_PASSWORD` and
  `DEMO_UID`.
- **Stack:** `compose.yaml` + `compose.prod.yaml` + `compose.demo.yaml`, with
  postgres, api, web and cloudflared running. The web host is on
  127.0.0.1:5400 only. The API, Postgres and the Caddy proxy are not public.
- **Cloudflare:** the tunnel is `VaultFlow`. Its route `vaultflow.fyi ->
  http://localhost:5400` must stay `http`. The DNS record is `@ CNAME
  <tunnel-id>.cfargotunnel.com`, proxied; deleting the route deletes it.
- **Sign-in today:** `owner` with `BOOTSTRAP_OWNER_PASSWORD` from the server's
  `.env`. 2FA for admins is off on this deployment, because the web login
  cannot enrol an authenticator.

**Committed but not yet running on the server:** commit `68545a3` adds
`Seeding:DevelopmentAccountPassword`. It replaces the published `cash1234`
when set, and `tools/Pos.DemoData` reads it from `VAULTFLOW_DEMO_PASSWORD`.
`compose.demo.yaml` wires both from `DEMO_ACCOUNT_PASSWORD` and adds a
one-shot `demodata` service. The code is copied to the server, but the API
image has **not** been rebuilt yet.

**Committed, not yet on the server:** the web host now passes each visitor's
address to the API (see "Shared login limit" below). This needs the newer
`compose.prod.yaml` and rebuilt **web** and **api** images, so copy the branch
over again before step 1 and build both: `$C up -d --build api web`.

## Next steps

### 1. Load the demo data (server)

```sh
cd ~/vaultflow
C="docker compose -f compose.yaml -f compose.prod.yaml -f compose.demo.yaml"
DEMO_SEED=true $C up -d --build api web
docker logs --since 2m vaultflow-api-1 | grep -i "seed complete\|development account"
$C --profile seed run --rm demodata      # roughly 10–30 min for 30 days
$C up -d api                              # seeding off again
```

- The seeder creates 14 staff accounts (owner, admin, warehouse, inventory,
  manager/2/3, cashier, cashier1b/2/2b/3/3b, auditor). All of them share
  `DEMO_ACCOUNT_PASSWORD`.
- **The existing `owner` password changes to `DEMO_ACCOUNT_PASSWORD`.**
- Leave seeding off afterwards. With it on, every API restart resets all 14
  passwords, including ones people have changed.
- For a quicker run, add `DEMO_DAYS=7` before the `run` command.

### 2. Give the demo accounts their own passwords

Everyone who knows `DEMO_ACCOUNT_PASSWORD` can sign in as the owner. Before
sharing any login:

- reset the owner and admin passwords from People & roles, or with
  `PUT /api/v1/.../users/{id}/password`;
- decide which accounts demo visitors get (for example `cashier` and
  `manager`) and give those a separate password.

### 3. Housekeeping

- **Rotate the tunnel token.** It was pasted in a chat. In Cloudflare, open
  Tunnels, then VaultFlow, then Refresh token. Replace `TUNNEL_TOKEN` in
  `.env`, then run `$C --profile tunnel up -d cloudflared`.
- **Turn off the bootstrap owner.** Set `BOOTSTRAP_OWNER_ENABLED=false` in
  `.env` once the owner password is changed.
- **Clean up the repo.** `DEV_CREDENTIALS.txt` is tracked in git. If anything
  in it is real, rotate it and remove the file from the repo.

### 4. Known issues worth a code change

- **Shared login limit — fixed in code, needs deploying.** Every public sign-in
  reached the API from the web container's address. That meant one shared
  limit of 10 sign-ins per 5 minutes. Worse, 20 failed sign-ins from that one
  address locked *every* visitor out for 15 minutes. The web host now sends the
  visitor's address and scheme to the API as `X-Forwarded-For` and
  `X-Forwarded-Proto` (`src/Pos.Web/Services/ForwardedCaller.cs`). It trusts
  the tunnel's `X-Forwarded-For` only from the networks listed in
  `ForwardedHeaders:KnownNetworks`, which `compose.prod.yaml` sets to Docker's
  private ranges. To check after deploying, sign in once with a wrong password,
  then look at the newest row: `docker compose ... exec postgres psql -U
  pos_migrator -d vaultflow -c 'select ip_address, attempted_at_utc from
  core.login_attempt order by attempted_at_utc desc limit 1'`. It should show
  your own public address, not a `172.x` one.
- **No 2FA enrolment on the web.** The web login can only take a 2FA code; it
  cannot enrol an authenticator, so Production admins are locked out unless 2FA
  is off. That is why `Security__RequireTwoFactorForAdmins=false` is set here.
- **HTTPS redirect warning — should stop after deploying.** The API logged
  "Failed to determine the https port for redirect" on every request, because
  the web host called it over plain HTTP. The forwarded `X-Forwarded-Proto:
  https` now tells it the visitor was already on HTTPS. The `demodata` run still
  calls the API directly, so expect the warning while it seeds.
- **API trusts any forwarded address.** `src/Pos.Api/Program.cs` clears the
  known proxy lists, so the API takes `X-Forwarded-For` from anyone who can
  reach it. On this deployment only the web container can. On the Render
  preview, where the API is public, a caller could pick their own rate-limit
  bucket. Populate the API's known networks from configuration when that
  matters.

## Updating the server after new commits

```sh
# on a machine with the repo and SSH access to the server
git archive deploy/vaultflow-fyi | ssh <server> 'cd ~/vaultflow && tar -x && rm -f DEV_CREDENTIALS.txt'
ssh <server> 'cd ~/vaultflow && docker compose -f compose.yaml -f compose.prod.yaml -f compose.demo.yaml --profile tunnel up -d --build'
```
