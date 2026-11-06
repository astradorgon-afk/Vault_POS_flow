# Published PWA browser smoke

Publish `Pos.Web` and run the published app from its output directory on a local HTTP port. Localhost is a secure context for service workers. Then run:

```powershell
$env:VAULTFLOW_PLAYWRIGHT_PACKAGE = '<path to installed Playwright package>'
node tests/Pos.Web.Pwa/offline-workflow.cjs http://localhost:5321
node tests/Pos.Web.Pwa/startup.cjs http://localhost:5321
node tests/Pos.Web.Pwa/web-styling.cjs http://localhost:5321
node tests/Pos.Web.Pwa/installability.cjs http://localhost:5321
```

When Playwright is installed in `node_modules`, omit `VAULTFLOW_PLAYWRIGHT_PACKAGE`.
The test mocks the same-origin API in the page, then checks enrollment, encrypted draft reload, offline inventory, scanner caching, reconnection, retry with the same event ID and sequence, two employees on one device, and concurrent offline tabs. It does not exercise a real API or phone camera.

The startup test uses a phone viewport and holds a runtime download to verify the static loading screen. It then checks successful startup and a readable retry screen when the download fails.

The website styling check blocks external Google font hosts and verifies that the phone login layout, local fonts, and stylesheet URLs still work. Set `VAULTFLOW_BROWSER_EXECUTABLE` to a browser executable to repeat this check in Brave; it also supports the active ngrok origin and its normal visitor notice.

The installability check validates the root `/login` manifest, Chrome's installation errors, both icons, and offline reopening after a browser restart. Repeat it against the HTTPS phone pilot URL. The browser still requires the user to accept installation on a physical phone.

Start a published phone pilot with `scripts/start-phone-pwa.ps1` while the API is running. This uses a temporary ngrok HTTPS address; visitors click through ngrok's one-time notice. The address changes on tunnel restart; use a permanent domain for real installations. The workflow check also verifies offline receiving and unexpected-goods details survive reload and never alter the downloaded available stock.

`real-gateway.cjs <https-pilot-origin>` is an opt-in local backend check. Supply `VAULTFLOW_PWA_TEST_PASSWORD` and optionally `VAULTFLOW_PWA_TEST_USER` (default `owner`) / `VAULTFLOW_API_ORIGIN` (default localhost:5177). It creates one temporary PWA device, enrolls through the real gateway, signs in, downloads scoped data, reloads offline, unlocks, and views inventory. It revokes the temporary device afterward and does not submit business operations. Use a test administrator and development database.
