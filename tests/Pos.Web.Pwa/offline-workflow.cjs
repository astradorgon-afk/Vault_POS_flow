const { chromium } = require(process.env.VAULTFLOW_PLAYWRIGHT_PACKAGE || 'playwright');

const origin = process.argv[2] || 'http://localhost:5321';
const deviceId = '3b690355-7f10-45ea-8968-c8491cbb6431';
const locationId = '3344f205-4478-44f5-aa2e-a23e98bb09bf';
const userId = '709c758d-f123-4d5b-a519-6619a11ca797';
const secondUserId = '4905272e-b03c-4269-97f0-7fe35af91aad';
const productId = 'ee4a8c8e-44cc-4c30-bbac-aea229612b2a';
const unitId = '71e7f562-1191-4531-9af4-71397327096c';
const orderId = '62c940fc-99d0-47b5-a32d-3fa3a60dde42';
const permissions = ['purchase.create', 'purchase.receive', 'inventory.view'];
const snapshot = {
  baseline: { cursor: 1, items: [{ type: 'LocationChanged', key: locationId,
    payload: { locationId, currencyCode: 'PHP' } }] },
  categories: [], units: [], suppliers: [],
  products: [{ id: productId, sku: 'TEST-1', name: 'Test rice', isActive: true,
    baseUnitOfMeasureId: unitId, defaultPurchaseCost: 11, barcodes: ['5901234123457'] }],
  stockLevels: { locationId, name: 'Pilot store', products: [{ productId, sku: 'TEST-1',
    name: 'Test rice', available: 12, inTransit: 2, onHold: 1, status: 'Healthy' }] },
  orders: [{ id: orderId, number: 'PO-PILOT', status: 'Ordered', destinationLocationId: locationId,
    lines: [{ id: productId, productId, productName: 'Test rice', orderedQuantity: 10, unitCost: 11 }] }],
  downloadedAtUtc: new Date().toISOString(),
};
// A realistic catalog-sized encrypted payload must survive storage and unlock;
// a tiny fixture would miss browser argument-limit failures during encoding.
for (let index = 0; index < 800; index++) snapshot.products.push({
  id: `00000000-0000-4000-8000-${String(index).padStart(12, '0')}`,
  sku: `CATALOG-${index}`, name: `Catalog fixture product ${index}`, isActive: true,
  baseUnitOfMeasureId: unitId, defaultPurchaseCost: 11, barcodes: [`FIXTURE-${index}`],
});

(async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    const context = await browser.newContext();
    await context.addInitScript(({ deviceId, locationId, userId, secondUserId, snapshot, permissions }) => {
      const nativeFetch = window.fetch.bind(window);
      const json = body => new Response(JSON.stringify(body), { status: 200,
        headers: { 'Content-Type': 'application/json' } });
      window.fetch = async (input, options = {}) => {
        const path = new URL(typeof input === 'string' ? input : input.url, location.href).pathname;
        if (!path.startsWith('/offline/api/')) return nativeFetch(input, options);
        if (!navigator.onLine) throw new TypeError('Failed to fetch');
        if (path.endsWith('/enrol')) {
          if (JSON.parse(options.body).osVersion.length > 64)
            return new Response('Device OS version exceeds the server storage limit.', { status: 400 });
          return json({ deviceId, locationId });
        }
        if (path.endsWith('/login')) {
          const signedInId = JSON.parse(options.body).userName === 'pilot2' ? secondUserId : userId;
          localStorage.setItem('pwa-smoke-online-user', signedInId);
          return json({ user: { userId: signedInId, displayName: signedInId === userId ? 'Pilot User' : 'Second User', permissions }, deviceId, locationId });
        }
        if (path.endsWith('/me')) {
          const signedInId = localStorage.getItem('pwa-smoke-online-user') || userId;
          return json({ user: { userId: signedInId, displayName: signedInId === userId ? 'Pilot User' : 'Second User', permissions }, deviceId, locationId });
        }
        if (path.endsWith('/snapshot')) return json(snapshot);
        if (path.endsWith('/pull')) return json({ nextCursor: 1, changes: [], errorCode: null });
        if (path.endsWith('/push')) {
          const event = JSON.parse(options.body).events[0];
          const uploads = JSON.parse(localStorage.getItem('pwa-smoke-uploads') || '[]');
          uploads.push(event);
          localStorage.setItem('pwa-smoke-uploads', JSON.stringify(uploads));
          if (uploads.length === 1) throw new TypeError('Uncertain response');
          return json({ results: [{ eventId: event.eventId, outcome: 'Accepted' }] });
        }
        return new Response(null, { status: 404 });
      };
    }, { deviceId, locationId, userId, secondUserId, snapshot, permissions });
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(`${origin}/offline/`);
    await page.getByRole('heading', { name: 'Welcome back' }).waitFor();
    await page.evaluate(async () => {
      const store = await import('/offline/js/offline-store.js');
      await store.enrol('pilot-code');
      await store.login('pilot', 'correct horse battery staple', '', '');
      await store.download();
      await store.lock();
    });
    await page.evaluate(() => navigator.serviceWorker.ready);
    await context.setOffline(true);
    await page.goto(`${origin}/login`);
    await context.setOffline(false);
    await page.getByText('Opening saved data').waitFor({ state: 'hidden' });
    await page.getByLabel('Username or email').fill('pilot');
    await page.getByLabel('Password', { exact: true }).fill('correct horse battery staple');
    await page.getByRole('button', { name: 'Open workspace' }).click();
    await page.getByRole('heading', { name: /Welcome, Pilot User/ }).waitFor();
    await page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('link', { name: 'Purchase orders' }).click();
    await page.getByRole('heading', { name: 'Purchase orders' }).waitFor();
    await page.getByRole('button', { name: '+ New purchase order' }).click();
    await page.getByLabel('Supplier name').fill('Pilot Supplier');
    await page.getByLabel('Search catalog').fill('rice');
    await page.getByRole('button', { name: 'Add', exact: true }).click();
    await page.getByRole('button', { name: 'Queue purchase draft' }).click();
    await page.getByText('Saved on device', { exact: true }).first().waitFor();
    await page.evaluate(() => navigator.serviceWorker.ready);
    const cachedScanner = await page.evaluate(async () => {
      const key = (await caches.keys()).find(name => name.startsWith('vaultflow-root-shell-'));
      return Boolean(key && await (await caches.open(key)).match('/offline/lib/zxing-wasm/zxing_reader.wasm'));
    });
    if (!cachedScanner) throw new Error('Scanner WASM was not cached.');
    await context.setOffline(true);
    await page.reload();
    await page.getByRole('heading', { name: 'Welcome back' }).waitFor();
    await page.getByText('Opening saved data').waitFor({ state: 'hidden' });
    await page.getByLabel('Username or email').fill('pilot');
    await page.getByLabel('Password', { exact: true }).fill('correct horse battery staple');
    await page.getByRole('button', { name: 'Open workspace' }).click();
    await page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('link', { name: 'Stock levels' }).click();
    await page.getByText('Available 12').waitFor();
    await page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('link', { name: /Pending work/ }).click();
    await page.getByText('Saved on device', { exact: true }).first().waitFor();
    await context.setOffline(false);
    await page.getByRole('button', { name: 'Sync pending work' }).click();
    await page.getByText('Confirmed', { exact: true }).waitFor();
    const uploads = await page.evaluate(() => JSON.parse(localStorage.getItem('pwa-smoke-uploads') || '[]'));
    if (uploads.length !== 2 || uploads[0].eventId !== uploads[1].eventId ||
      uploads[0].deviceSequence !== uploads[1].deviceSequence)
      throw new Error(`Retry identity changed: ${JSON.stringify(uploads)}`);
    await page.getByRole('button', { name: 'Lock workspace' }).click();
    await page.evaluate(async () => {
      const store = await import('/offline/js/offline-store.js');
      await store.login('pilot2', 'correct horse battery staple', '', '');
      await store.download();
      await store.lock();
    });
    await context.setOffline(true);
    await page.goto(`${origin}/login`);
    await context.setOffline(false);
    await page.getByText('Opening saved data').waitFor({ state: 'hidden' });
    await page.getByLabel('Username or email').fill('pilot2');
    await page.getByLabel('Password', { exact: true }).fill('correct horse battery staple');
    await page.getByRole('button', { name: 'Open workspace' }).click();
    await page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('link', { name: /Pending work/ }).click();
    await page.getByText('No local operations yet.').waitFor();
    await page.getByRole('button', { name: 'Lock workspace' }).click();
    await page.getByLabel('Username or email').fill('pilot');
    await page.getByLabel('Password', { exact: true }).fill('correct horse battery staple');
    await page.getByRole('button', { name: 'Open workspace' }).click();
    await page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('link', { name: /Pending work/ }).click();
    await page.getByText('Confirmed', { exact: true }).waitFor();
    await context.setOffline(true);
    const secondTab = await context.newPage();
    await secondTab.goto(`${origin}/login`);
    await secondTab.getByRole('heading', { name: 'Welcome back' }).waitFor();
    await secondTab.getByText('Opening saved data').waitFor({ state: 'hidden' });
    await secondTab.getByLabel('Username or email').fill('pilot');
    await secondTab.getByLabel('Password', { exact: true }).fill('correct horse battery staple');
    await secondTab.getByRole('button', { name: 'Open workspace' }).click();
    const queueInTab = async (tab, supplier) => {
      await tab.getByRole('navigation', { name: 'Primary navigation' }).getByRole('link', { name: 'Purchase orders' }).click();
      await tab.getByRole('heading', { name: 'Purchase orders' }).waitFor();
      const newOrder = tab.getByRole('button', { name: '+ New purchase order' });
      if (await newOrder.isVisible()) await newOrder.click();
      else await tab.getByRole('heading', { name: 'New purchase order' }).waitFor();
      await tab.getByLabel('Supplier name').fill(supplier);
      await tab.getByLabel('Search catalog').fill('rice');
      await tab.getByRole('button', { name: 'Add', exact: true }).click();
      await tab.getByRole('button', { name: 'Queue purchase draft' }).click();
      await tab.getByText('Saved on device', { exact: true }).first().waitFor();
    };
    await Promise.all([queueInTab(page, 'First tab supplier'), queueInTab(secondTab, 'Second tab supplier')]);
    const sequences = await page.evaluate(async () => {
      const opened = indexedDB.open('vaultflow-pwa-v1');
      const db = await new Promise((resolve, reject) => {
        opened.onsuccess = () => resolve(opened.result);
        opened.onerror = () => reject(opened.error);
      });
      const request = db.transaction('outbox').objectStore('outbox').getAll();
      return new Promise((resolve, reject) => {
        request.onsuccess = () => resolve(request.result.map(item => item.sequence));
        request.onerror = () => reject(request.error);
      });
    });
    if (sequences.length !== 3 || new Set(sequences).size !== 3)
      throw new Error(`Two tabs did not allocate distinct sequences: ${sequences}`);
    await page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('link', { name: 'Goods receiving' }).click();
    await page.getByLabel(/^Order/).selectOption(orderId);
    await page.getByLabel('Barcode', { exact: true }).fill('5901234123457');
    await page.getByRole('button', { name: 'Count one', exact: true }).click();
    await page.getByLabel('Barcode', { exact: true }).fill('UNKNOWN-QUARANTINE');
    await page.getByRole('button', { name: 'Count one', exact: true }).click();
    await page.getByLabel('Description', { exact: true }).fill('Unexpected box');
    await page.getByRole('button', { name: 'Save count on device' }).click();
    await page.getByText('Physical count saved on device.', { exact: true }).waitFor();
    await page.reload();
    await page.getByText('Opening saved data').waitFor({ state: 'hidden' });
    await page.getByLabel('Username or email').fill('pilot');
    await page.getByLabel('Password', { exact: true }).fill('correct horse battery staple');
    await page.getByRole('button', { name: 'Open workspace' }).click();
    await page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('link', { name: 'Goods receiving' }).click();
    if (await page.getByLabel('Arrived', { exact: true }).inputValue() !== '1')
      throw new Error('Receiving quantity did not survive an offline reload.');
    if (await page.getByLabel('Description', { exact: true }).inputValue() !== 'Unexpected box')
      throw new Error('Quarantine detail did not survive an offline reload.');
    await page.getByRole('button', { name: 'Queue receiving count' }).click();
    await page.getByText('Count saved on device. Stock remains pending server confirmation.', { exact: true }).waitFor();
    await page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('link', { name: 'Stock levels' }).click();
    await page.getByText('Available 12').waitFor();
    if (errors.length) throw new Error(errors.join('; '));
    console.log('Published PWA workflow passed: offline unlock, inventory, draft, receiving/quarantine reload, idempotent retry, employee separation and two tabs.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
