// Opt-in local pilot check. Creates and revokes one temporary PWA device;
// it does not submit purchases, receiving records, or stock movements.
const { chromium, request } = require(process.env.VAULTFLOW_PLAYWRIGHT_PACKAGE || 'playwright');
const assert = require('node:assert/strict');
const origin = process.argv[2];
const apiOrigin = process.env.VAULTFLOW_API_ORIGIN || 'http://localhost:5177';
const userName = process.env.VAULTFLOW_PWA_TEST_USER || 'owner';
const password = process.env.VAULTFLOW_PWA_TEST_PASSWORD;

(async () => {
  assert.ok(origin && password, 'Supply the HTTPS pilot URL and VAULTFLOW_PWA_TEST_PASSWORD for a local test administrator.');
  const api = await request.newContext({ baseURL: apiOrigin });
  let browser, admin, deviceId;
  const json = async (response, action) => {
    assert.ok(response.ok(), `${action} returned HTTP ${response.status()}`);
    return response.json();
  };
  try {
    const auth = await json(await api.post('/api/v1/auth/login', { data: { userName, password } }), 'Administrator sign-in');
    admin = await request.newContext({ baseURL: apiOrigin, extraHTTPHeaders: { Authorization: `Bearer ${auth.accessToken}` } });
    const locations = await json(await admin.get('/api/v1/locations'), 'Location lookup');
    const location = locations.find(row => row.isActive && row.code === 'STORE01');
    assert.ok(location, 'An active internal location is required.');
    const registration = await json(await admin.post('/api/v1/devices', { data: {
      shortCode: `T${require('node:crypto').randomBytes(3).toString('hex').slice(0, 5)}`.toUpperCase(),
      name: 'PWA gateway verification (temporary)', locationId: location.id, platform: 4,
    } }), 'PWA device registration');
    deviceId = registration.deviceId;
    console.log('Real API: temporary PWA device registered.');
    browser = await chromium.launch();
    const context = await browser.newContext();
    const page = await context.newPage();
    await page.goto(`${origin}/offline/`);
    await page.getByRole('heading', { name: 'Enroll this installation' }).waitFor();
    await page.waitForFunction(() => window.vaultFlowOffline?.ready === true, {}, { timeout: 120000 });
    console.log('Real API: published app files cached.');
    await page.getByLabel('Enrollment code').fill(registration.enrolmentCode);
    const enrolled = page.waitForResponse(response => response.url().endsWith('/offline/api/enrol'));
    await page.getByRole('button', { name: 'Enroll browser' }).click();
    const enrollmentResponse = await enrolled;
    if (!enrollmentResponse.ok()) {
      let detail;
      try { const body = await enrollmentResponse.json(); detail = body.detail || body.message || body.title; } catch { }
      throw new Error(`Gateway enrollment returned HTTP ${enrollmentResponse.status()}: ${detail || 'no detail'}`);
    }
    console.log('Real API: gateway enrollment accepted.');
    const deviceLogin = await json(await api.post('/api/v1/auth/login', {
      headers: { 'X-Device-Id': deviceId }, data: { userName, password },
    }), 'Device-bound administrator sign-in');
    const deviceAdmin = await request.newContext({ baseURL: apiOrigin, extraHTTPHeaders: {
      Authorization: `Bearer ${deviceLogin.accessToken}`, 'X-Device-Id': deviceId,
    } });
    try {
      const eligible = await json(await deviceAdmin.get('/api/v1/pwa/employees'), 'Assigned employee list');
      const manager = eligible.find(employee => employee.userName === 'manager');
      assert.ok(manager, 'Store One manager must be eligible for this installation.');
      const delegated = await json(await deviceAdmin.post('/api/v1/pwa/provision-token', {
        data: { userId: manager.userId },
      }), 'Manager snapshot identity');
      const restricted = await request.newContext({ baseURL: apiOrigin, extraHTTPHeaders: {
        Authorization: `Bearer ${delegated.accessToken}`, 'X-Device-Id': deviceId,
      } });
      try {
        assert.equal((await restricted.get('/api/v1/sync/baseline')).status(), 200);
        assert.equal((await restricted.get('/api/v1/users')).status(), 403);
        assert.equal((await restricted.post('/api/v1/sync/push', { data: {} })).status(), 403);
      } finally { await restricted.dispose(); }
      const otherLocation = await json(await admin.get('/api/v1/users'), 'User list');
      const manager2 = otherLocation.find(employee => employee.userName === 'manager2');
      if (manager2) assert.equal((await deviceAdmin.post('/api/v1/pwa/provision-token', {
        data: { userId: manager2.userId ?? manager2.id },
      })).status(), 403, 'An employee from another store cannot be prepared.');
    } finally { await deviceAdmin.dispose(); }
    await page.getByRole('heading', { name: 'Open your workspace' }).waitFor();
    await page.getByLabel('Username or email').fill(userName);
    await page.getByLabel('Password').fill(password);
    const signedIn = page.waitForResponse(response => response.url().endsWith('/offline/api/login'));
    const downloaded = page.waitForResponse(response => response.url().endsWith('/offline/api/snapshot'), { timeout: 120000 });
    // If login fails, do not leave an unrelated snapshot wait unhandled.
    downloaded.catch(() => {});
    await page.getByRole('button', { name: 'Sign in and download' }).click();
    assert.ok((await signedIn).ok(), 'Gateway sign-in failed.');
    assert.ok((await downloaded).ok(), 'Gateway snapshot download failed.');
    await page.waitForFunction(() => Array.from(document.querySelectorAll('.notice, .error')).some(node =>
      node.textContent.includes('Workspace ready on this device.') ||
      node.textContent.includes('Download needs attention:') || node.classList.contains('error')), {}, { timeout: 120000 });
    const downloadWarning = page.getByText(/Signed in\. Download needs attention:/);
    if (await downloadWarning.count()) throw new Error(await downloadWarning.innerText());
    const failure = page.locator('.error');
    if (await failure.count()) throw new Error(await failure.innerText());
    await page.getByText('Workspace ready on this device.', { exact: true }).waitFor({ timeout: 120000 });
    const scope = await page.evaluate(async () => (await import('./js/offline-store.js')).currentScope());
    await page.getByRole('button', { name: 'Prepare employees' }).click();
    await page.getByRole('heading', { name: 'Prepare employees for offline use' }).waitFor();
    await page.locator('article.operation').first().waitFor();
    const pins = new Map();
    for (const user of ['manager', 'cashier']) {
      const row = page.locator('article.operation').filter({
        has: page.locator('small', { hasText: new RegExp(`^${user}$`) }),
      });
      await row.getByRole('button', { name: 'Generate PIN and prepare' }).click();
      await row.getByText('Offline PIN:').waitFor({ timeout: 120000 });
      const pin = (await row.innerText()).match(/Offline PIN:\s*(\d{8})/)?.[1];
      assert.ok(pin, `${user} was not given an offline PIN`);
      pins.set(user, pin);
    }
    assert.notEqual(pins.get('manager'), pins.get('cashier'), 'Employees need distinct offline PINs.');
    const prepared = await page.evaluate(async () => (await import('./js/offline-store.js')).initialize());
    assert.equal(prepared.profiles.filter(profile => profile.unlockMethod === 'pin').length, 2);
    await context.setOffline(true);
    await page.reload();
    for (const user of ['manager', 'cashier']) {
      const profile = prepared.profiles.find(profile => profile.userName === user);
      assert.ok(profile, `${user} local profile missing`);
      await page.getByLabel('Employee').selectOption(profile.scope);
      await page.getByLabel('Offline PIN').fill(pins.get(user));
      await page.getByRole('button', { name: 'Unlock saved data' }).click();
      await page.getByRole('heading', { name: user === 'manager' ? 'Carmela Reyes' : 'Joy Mendoza' }).waitFor();
      if (user === 'cashier') {
        assert.equal(await page.getByRole('button', { name: 'Purchase draft' }).count(), 0);
        assert.equal(await page.getByRole('button', { name: 'Receiving', exact: true }).count(), 0);
      }
      await page.getByRole('button', { name: 'Lock', exact: true }).click();
    }
    await page.getByLabel('Employee').selectOption(scope);
    await page.getByLabel('Password').fill(password);
    await page.getByRole('button', { name: 'Unlock saved data' }).click();
    await page.getByRole('button', { name: 'Inventory view', exact: true }).click();
    await page.getByRole('heading', { name: 'Downloaded inventory' }).waitFor();
    await page.getByRole('alert').waitFor({ state: 'hidden' });
    console.log('Real gateway passed: registration, sign-in, two administrator-prepared employee PINs, isolated offline unlocks, role-specific workflows, and owner password unlock.');
  } finally {
    await browser?.close();
    if (deviceId && admin) {
      const response = await admin.post(`/api/v1/devices/${deviceId}/revoke`, { data: { reason: 'Temporary PWA verification completed' } });
      assert.ok(response.ok(), `Temporary device cleanup returned HTTP ${response.status()}`);
    }
    await admin?.dispose();
    await api.dispose();
  }
})().catch(error => { console.error(error.message); process.exitCode = 1; });
