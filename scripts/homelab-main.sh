#!/usr/bin/env bash
# Run from ~/vaultflow on the main laptop. Never rebuild or migrate here.
set -Eeuo pipefail
cd "$(dirname "$0")/.."

if [[ "$(cat /etc/machine-id)" != f1e81347799b48d2b5cdedcd925ff2da ]]; then
    echo 'This script is configured for the main home-server laptop.' >&2
    exit 1
fi

app=(docker compose -f compose.yaml -f compose.prod.yaml -f compose.demo.yaml)
gateway=(docker compose --env-file homelab.env -f compose.homelab.gateway.yaml)

rollback() {
    "${gateway[@]}" down
    "${app[@]}" up -d --no-deps --no-build web
}

case "${1:-status}" in
    activate)
        "${app[@]}" -f compose.homelab.main.yaml config --quiet
        "${gateway[@]}" config --quiet
        curl --fail --silent --max-time 15 http://100.74.92.104:5403/login >/dev/null
        trap 'echo "Cutover failed; restoring the original web port." >&2; rollback' ERR
        "${app[@]}" -f compose.homelab.main.yaml up -d --no-deps --no-build web
        "${gateway[@]}" up -d
        ready=false
        for attempt in {1..30}; do
            if curl --fail --silent --max-time 3 http://127.0.0.1:5400/login >/dev/null; then
                ready=true
                break
            fi
            sleep 1
        done
        "$ready"
        trap - ERR
        echo 'Load balancer active on the existing Cloudflare origin port.'
        ;;
    rollback)
        rollback
        echo 'Original single-laptop website restored. Stop the worker separately if retiring it.'
        ;;
    status)
        "${gateway[@]}" ps
        "${app[@]}" -f compose.homelab.main.yaml ps
        curl --fail --silent 'http://127.0.0.1:5490/stats;csv' |
            awk -F, '$1 == "website_nodes" {print $2, $18, "requests=" $8, "weight=" $19}'
        ;;
    *)
        echo 'Usage: bash scripts/homelab-main.sh [activate|status|rollback]' >&2
        exit 2
        ;;
esac
