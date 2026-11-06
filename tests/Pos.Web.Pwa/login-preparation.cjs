const { chromium } = require(process.env.VAULTFLOW_PLAYWRIGHT_PACKAGE || 'playwright');

const origin = process.argv[2] || 'http://127.0.0.1:5434';
const deviceId = '3b690355-7f10-45ea-8968-c8491cbb6431';
const locationId = '3344f205-4478-44f5-aa2e-a23e98bb09bf';
const adminId = '709c758d-f123-4d5b-a519-6619a11ca797';
const employeeId = '4905272e-b03c-4269-97f0-7fe35af91aad';
const scope = `${employeeId}:${locationId}`;
const permissions = ['purchase.create', 'inventory.view'];
const employee = { userId: employeeId, displayName: 'Pilot Employee', permissions };
const snapshot = {
  baseline: { cursor: 1, items: [] }, categories: [], units: [], suppliers: [],
  products: [], stockLevels: { locationId, name: 'Pilot store', products: [] },
  orders: [], downloadedAtUtc: new Date().toISOString(),
};

(async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    const context = await browser.newContext();
    await context.addInitScript(({ deviceId, locationId, adminId, employee, snapshot }) => {
      const original = window.fetch.bind(window);
      const json = value => new Response(JSON.stringify(value), { status: 200,
        headers: { 'Content-Type': 'application/json' } });
      window.fetch = async (input, options = {}) => {
        const path = new URL(typeof input === 'string' ? input : input.url, location.href).pathname;
        if (!path.startsWith('/offline/api/')) return original(input, options);
        if (!navigator.onLine) throw new TypeError('Failed to fetch');
        if (path.endsWith('/enrol')) return json({ deviceId, locationId });
        if (path.endsWith('/login')) return json({ user: {
          userId: adminId, displayName: 'Pilot Admin', permissions: ['purchase.create'] },
          deviceId, locationId });
        if (path.endsWith('/provision')) return json({ employee: {
          ...employee, userName: 'pilot' }, snapshot });
        if (path.endsWith('/handoff')) {
          localStorage.setItem('test-handoff-user', employee.userId);
          return json({ user: employee, deviceId, locationId });
        }
        if (path.endsWith('/me')) return json({ user: employee, deviceId, locationId });
        if (path.endsWith('/snapshot')) return json(snapshot);
        if (path.endsWith('/pull')) return json({ nextCursor: 1, changes: [] });
        if (path.endsWith('/logout')) return json({});
        return new Response(null, { status: 404 });
      };
    }, { deviceId, locationId, adminId, employee, snapshot });

    const page = await context.newPage();
    await page.goto(origin + '/login');
    await page.getByRole('heading', { name: 'Welcome back' }).waitFor();
    await page.evaluate(() => navigator.serviceWorker.ready);
    const result = await page.evaluate(async ({ scope, employeeId }) => {
      const store = await import('/offline/js/offline-store.js');
      await store.enrol('pilot-enrolment');
      await store.login('admin', 'admin-password', '', '');
      await store.provision(employeeId, '12345678');
      await store.lock();
      await store.unlock(scope, '12345678');
      const eventId = await store.queue('PwaPurchaseDraft', { supplier: 'Before migration' }, 'Pending purchase');
      await store.saveWorking('purchase', { supplier: 'Saved draft' });
      await store.lock();
      const first = await store.prepareFromHandoff('test-token', 'pilot', 'account-password');
      if (first.status !== 'old-secret-required' || first.secretKind !== 'pin')
        throw new Error(`Legacy PIN was not requested: ${JSON.stringify(first)}`);
      let wrongPinRejected = false;
      try { await store.prepareFromHandoff('', 'pilot', 'account-password', 'wrong-pin'); }
      catch { wrongPinRejected = true; }
      if (!wrongPinRejected) throw new Error('Wrong previous PIN was accepted.');
      const migrated = await store.prepareFromHandoff('', 'pilot', 'account-password', '12345678');
      if (migrated.status !== 'prepared') throw new Error('Migration did not finish.');
      await store.lock();
      await store.unlock(scope, 'account-password');
      const operations = await store.operations();
      const payload = await store.operationPayload(eventId);
      const draft = await store.loadWorking('purchase');
      await store.lock();
      return { eventId, operations, payload, draft, state: await store.initialize() };
    }, { scope, employeeId });
    if (result.operations.length !== 1 || result.operations[0].eventId !== result.eventId ||
      result.operations[0].status !== 'Saved on device' ||
      result.payload.supplier !== 'Before migration' || result.draft.supplier !== 'Saved draft')
      throw new Error('Migration changed saved work or queue identity.');
    if (result.state.profiles.find(profile => profile.scope === scope)?.unlockMethod !== 'password')
      throw new Error('Migrated profile still uses the old PIN.');

    await context.setOffline(true);
    await page.goto(origin + '/login');
    await page.getByRole('heading', { name: 'Welcome back' }).waitFor();
    await page.getByText('Opening saved data').waitFor({ state: 'hidden' });
    if (await page.getByLabel('Enroll installation').count())
      throw new Error('Enrollment is still shown for an enrolled installation.');
    await page.getByLabel('Username or email').fill('pilot');
    await page.getByLabel('Password', { exact: true }).fill('account-password');
    await page.getByRole('button', { name: 'Open workspace' }).click();
    await page.getByRole('heading', { name: 'Welcome, Pilot Employee' }).waitFor();
    console.log('Preparation passed: legacy PIN migrated, saved work retained, account password unlocks at /login.');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
