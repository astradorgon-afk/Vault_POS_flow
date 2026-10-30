"""Read-only checks of web routing, Blazor interactivity and browser affinity.

Run against the staging SSH forward, then the public HTTPS origin after cutover.
No credentials or business records are used.
"""
import argparse
import json
from collections import Counter
from pathlib import Path

from playwright.sync_api import sync_playwright


def run(origin: str, output: Path) -> None:
    output.mkdir(parents=True, exist_ok=True)
    report = {"origin": origin, "new_visitors": {}, "nodes": []}
    with sync_playwright() as p:
        counts = Counter()
        for _ in range(9):
            client = p.request.new_context()
            try:
                response = client.get(origin + "/login")
                assert response.status == 200, response.status
                counts[response.headers.get("x-vaultflow-node")] += 1
            finally:
                client.dispose()
        assert counts == {"main": 6, "worker": 3}, counts
        report["new_visitors"] = dict(counts)

        browser = p.chromium.launch(headless=True)
        try:
            for node in ("main", "worker"):
                context = browser.new_context(service_workers="block")
                try:
                    context.add_cookies([{
                        "name": "vaultflow.route", "value": node,
                        "url": origin, "httpOnly": True,
                        "secure": origin.startswith("https:"), "sameSite": "Lax",
                    }])
                    page = context.new_page()
                    errors = []
                    sockets = []
                    page.on("pageerror", lambda error: errors.append(str(error)))
                    page.on("websocket", lambda socket: sockets.append(socket.url))
                    for _ in range(3):
                        response = page.goto(origin + "/login")
                        assert response.status == 200
                        assert response.headers.get("x-vaultflow-node") == node
                        page.wait_for_load_state("networkidle")
                        page.get_by_role("heading", name="Welcome back").wait_for()
                        # Prerendered controls can appear before the slower
                        # laptop finishes attaching its interactive circuit.
                        page.wait_for_function("document.querySelector('button[type=submit]')?.disabled === false")
                        # The deployed release has an options panel; newer
                        # releases expose the OTP field through its own button.
                        options = page.get_by_role("button", name="Additional sign-in options")
                        if options.count():
                            options.click()
                            page.get_by_label("Two-factor authentication code").wait_for()
                            # Opening the panel loads public sign-in locations
                            # from the actual API, exercising the worker's relay.
                            page.wait_for_function("document.querySelector('#work-location')?.options.length > 1")
                            options.click()
                            page.locator("#work-location").wait_for(state="detached")
                        else:
                            toggle = page.get_by_role("button", name="Have a two-factor code?")
                            toggle.click()
                            page.get_by_label("Two-factor code").wait_for()
                            toggle.click()
                            page.get_by_label("Two-factor code").wait_for(state="detached")
                    assert any("/_blazor" in url for url in sockets), sockets
                    assert not errors, errors
                    page.screenshot(path=str(output / f"{node}-login.png"), full_page=True)
                    report["nodes"].append({"node": node, "reloads": 3,
                                            "interactive": True, "websockets": len(sockets)})
                finally:
                    context.close()
        finally:
            browser.close()
    (output / "result.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("origin")
    parser.add_argument("--output", type=Path, default=Path("artifacts/homelab/smoke"))
    args = parser.parse_args()
    run(args.origin.rstrip("/"), args.output)
