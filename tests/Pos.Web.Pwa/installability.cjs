const { chromium, devices } = require(process.env.VAULTFLOW_PLAYWRIGHT_PACKAGE || 'playwright');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const assert = require('node:assert/strict');
const origin = process.argv[2] || 'http://localhost:5321';

(async () => {
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'vaultflow-pwa-install-'));
  let context;
  try {
    const options = { ...devices['Pixel 5'], headless: true };
    context = await chromium.launchPersistentContext(profile, options);
    const page = await context.newPage();
    await page.goto(`${origin}/login`);
    if (origin.includes('ngrok-free.')) {
      await page.getByText('Visit Site', { exact: true }).click();
    }
    await page.getByRole('heading', { name: 'Welcome back' }).waitFor();
    await page.waitForFunction(() => typeof window.vaultFlowInstall?.prompt === 'function');

    const cdp = await context.newCDPSession(page);
    const manifest = await cdp.send('Page.getAppManifest');
    assert.deepEqual(manifest.errors, [], JSON.stringify(manifest.errors));
    assert.equal(manifest.manifest.name, 'VaultFlow');
    assert.equal(manifest.manifest.id, `${origin}/`);
    assert.equal(manifest.manifest.scope, `${origin}/`);
    assert.equal(manifest.manifest.display, 'kStandalone');
    assert.deepEqual((await cdp.send('Page.getInstallabilityErrors')).installabilityErrors, []);
    for (const size of [192, 512]) {
      const icon = await page.request.get(`${origin}/icons/icon-${size}.png`);
      assert.equal(icon.status(), 200);
      assert.match(icon.headers()['content-type'], /image\/png/);
      const png = await icon.body();
      assert.equal(png.readUInt32BE(16), size);
      assert.equal(png.readUInt32BE(20), size);
    }
    await page.evaluate(() => navigator.serviceWorker.ready);
    await page.waitForFunction(async () => (await caches.keys()).some(key => key.startsWith('vaultflow-root-shell-')));

    await context.close();
    context = await chromium.launchPersistentContext(profile, { ...options, offline: true });
    const reopened = await context.newPage();
    await reopened.goto(`${origin}/login`);
    await reopened.getByRole('heading', { name: 'Welcome back' }).waitFor();
    console.log('PWA installability passed: root manifest, icons, browser install checks, and offline login after restart.');
  } finally {
    await context?.close();
    fs.rmSync(profile, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
