# Two-laptop home deployment

The main laptop (`homecomputer`, Tailscale `100.98.212.11`, LAN `192.168.0.120`)
keeps Postgres, the API, scheduled jobs, Cloudflare tunnel and a web instance.
The worker (`homecomputer-worker`, Tailscale `100.74.92.104`, LAN `192.168.0.174`)
runs a second copy of the **same deployed web image**. No second database or API
is started, so scheduled inventory and sync jobs are not duplicated.

Cloudflare still targets `http://localhost:5400`. HAProxy on that port distributes
new browser sessions with weights main=2, worker=1. Its Secure, HttpOnly,
SameSite=Lax `vaultflow.route` cookie keeps every path, including Blazor WebSocket
and PWA gateway requests, on the chosen laptop. Balancing is by browser session,
not individual page request. The ratio reflects four main CPU threads versus two
worker threads and can be changed in `build/docker/homelab-main.cfg`.

## Ports and access

| Laptop | Listener | Purpose |
| --- | --- | --- |
| Main | `127.0.0.1:5400` | Existing tunnel origin, now the balancer |
| Main | `127.0.0.1:5402` | Main web instance |
| Main | `100.98.212.11:5480` | API relay, accepts only worker `100.74.92.104` |
| Main | `127.0.0.1:5481` | Private Docker relay behind the API allowlist |
| Main | `127.0.0.1:5490/stats` | Backend status and request counters |
| Worker | `100.74.92.104:5403` | Web ingress, accepts only main `100.98.212.11` |
| Worker | `127.0.0.1:5402` | Worker web instance |

Laptop-to-laptop traffic uses Tailscale. The API allowlist runs with host
networking because Docker's published-port proxy rewrites the original source
address to the bridge gateway address. Neither the database nor an unrestricted
API listener is published. The worker has no database password or JWT signing key.

## Files and normal operations

Main files live in `~/vaultflow`; worker files live in `~/vaultflow-worker`.
Each has `homelab.env` with `HAPROXY_IMAGE` pinned to a tested image digest and
`VAULTFLOW_WEB_IMAGE` naming the exact web image exported from the main laptop.
The worker Compose file and configs are copied from this repository. Deployment
does not copy the main laptop's `.env` or `.secrets` to the worker.

On the main laptop:

```sh
cd ~/vaultflow
bash scripts/homelab-main.sh status
# Apply the main web port overlay and switch the gateway to port 5400:
bash scripts/homelab-main.sh activate
# Restore the previous direct-to-web tunnel origin:
bash scripts/homelab-main.sh rollback
```

On the worker:

```sh
cd ~/vaultflow-worker
sudo docker compose --env-file homelab.env -f compose.homelab.worker.yaml ps
sudo docker compose --env-file homelab.env -f compose.homelab.worker.yaml up -d
# Stop only the VaultFlow worker, retaining its key volume:
sudo docker compose --env-file homelab.env -f compose.homelab.worker.yaml down
```

Future main web deployments must include the overlay **last**:

```sh
docker compose -f compose.yaml -f compose.prod.yaml -f compose.demo.yaml \
  -f compose.homelab.main.yaml up -d --no-deps web
```

Export the new main web image with a unique tag, transfer it over SSH, load it
on the worker, update `VAULTFLOW_WEB_IMAGE` in its `homelab.env`, and recreate
only the worker web service. Keep the replicas on the same release. A web restart
ends that instance's in-memory sessions and Blazor circuits; users sign in again.
Data-protection keys remain in each instance's persistent volume. Session and
handoff stores are process-local, so copying keys alone does not enable session
transfer between replicas.

## Failure behavior and checks

The balancer checks `/login` every three seconds, removes a node after three
failed checks, and restores it after two successful checks. If the worker stops,
requests go to the main laptop; affected browsers may need to reload and sign
in. This is added web capacity and worker failover. The main laptop remains
required for the database, API, tunnel and balancer.

Run the read-only browser smoke against an SSH-forwarded staging listener, then
against the public origin. It checks both nodes, sticky routing through reloads,
and live Blazor WebSocket interactions without credentials or business writes:

```sh
python tests/homelab/smoke.py https://vaultflow.fyi --output artifacts/homelab/live
```

For a maintenance-window failover check, stop the worker ingress, wait for it to
be marked DOWN, request `/login` with `Cookie: vaultflow.route=worker`, and verify
`X-VaultFlow-Node: main`. Restart ingress and verify recovery before ending work.

Radarr, Sonarr, Prowlarr, Bazarr, qBittorrent, FlareSolverr and Jellyseerr remain
stopped on the worker with Docker restart policy `no`. Their data was retained;
the original policies are recorded under `~/media-stop-20261023-163601` there.
Do not bring up `/opt/mediastack/docker-compose.yml` while reserving that laptop
for VaultFlow, as Compose can recreate those containers with their old policies.

This routing follows the [Blazor hosting guidance on session affinity](https://learn.microsoft.com/en-us/aspnet/core/blazor/host-and-deploy/server/)
and [HAProxy's cookie persistence configuration](https://docs.haproxy.org/3.2/configuration.html#cookie).

## Verified deployment, 24 October 2026 (Philippines)

- Both replicas use image ID `f08d5f6d5a768b665070e3d8a4712b4d6cc3665ea64f4fcb8c1535464c1eb250`,
  exported from deployed release `3f71a95`; application code was not rebuilt.
- HAProxy is pinned to digest `dfba86f031bd4c9039f10f8cc5de75991d1b6766d5460f0335b980408503baad`.
- Staging and public HTTPS smoke each observed six main and three worker visits.
  Both nodes passed three browser reloads with live Blazor WebSockets and loaded
  the API's real sign-in locations. The tests did not sign in or change business data.
- With worker ingress stopped, a request pinned to `worker` returned HTTP 200
  from `main`. After restarting ingress, the same request returned HTTP 200 from
  `worker`. The test ran on staging before public cutover.
- The worker received HTTP 200 from the API health endpoint. This desktop received
  HTTP 403 from both private Tailscale listeners, confirming their source allowlists.
- API and Postgres containers retained their original start times during cutover.
  All seven media containers remained stopped with restart policy `no`.
- Local evidence: `artifacts/homelab/staging/result.json`,
  `artifacts/homelab/live/result.json`, screenshots beside those files, and
  `artifacts/homelab/failover.json`. The exported image is retained in
  `~/vaultflow/homelab-backup/web-image.tar.gz` on the main laptop.
