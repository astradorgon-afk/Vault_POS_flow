const assert = require('node:assert/strict');
const { chromium, devices } = require(process.env.VAULTFLOW_PLAYWRIGHT_PACKAGE || 'playwright');
const origin = process.argv[2] || 'http://localhost:5321';

async function cacheReady(page) {
    return page.evaluate(async () => {
        const keys = (await caches.keys()).filter(name => name.startsWith('vaultflow-root-shell-'));
        for (const key of keys) {
            if (await (await caches.open(key)).match('/offline/cache-ready')) return true;
        }
        return false;
    });
}

(async () => {
    const browser = await chromium.launch({ headless: true });
    try {
        const context = await browser.newContext(devices['Pixel 5']);
        const page = await context.newPage();
        const errors = [];
        page.on('console', entry => { if (entry.type() === 'error') errors.push(entry.text()); });
        page.on('pageerror', error => errors.push(error.message));
        await page.goto(`${origin}/offline/`, { waitUntil: 'domcontentloaded' });
        await page.getByRole('heading', { name: 'Welcome back' }).waitFor({ timeout: 60000 });
        await page.waitForFunction(async () => {
            const keys = (await caches.keys()).filter(name => name.startsWith('vaultflow-root-shell-'));
            for (const key of keys)
                if (await (await caches.open(key)).match('/offline/cache-ready')) return true;
            return false;
        }, null, { timeout: 60000 });

        await page.evaluate(async () => {
            await Promise.all((await caches.keys()).filter(name => name.startsWith('vaultflow-root-shell-'))
                .map(name => caches.delete(name)));
        });
        assert.equal(await cacheReady(page), false);

        await page.goto(`${origin}/offline/`, { waitUntil: 'domcontentloaded' });
        await page.waitForFunction(() => window.vaultFlowOffline?.ready === true,
            null, { timeout: 60000 }).catch(async error => {
                console.error('Recovery state:', await page.evaluate(async () => ({
                    warning: document.getElementById('pwa-cache-warning')?.textContent,
                    cacheNames: await caches.keys(),
                    worker: (await navigator.serviceWorker.getRegistration('/'))?.active?.state,
                    ready: window.vaultFlowOffline?.ready,
                })));
                console.error('Browser errors:', errors.slice(0, 8));
                throw error;
            });
        assert.equal(await cacheReady(page), true);
        await page.getByRole('heading', { name: 'Welcome back' }).waitFor();

        await context.setOffline(true);
        await page.goto(`${origin}/offline/`, { waitUntil: 'domcontentloaded' });
        await page.getByRole('heading', { name: 'Welcome back' }).waitFor({ timeout: 30000 });
        await context.close();
        console.log('Browser cache recovery passed: online repair restored offline startup.');
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
