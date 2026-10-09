const { chromium, devices } = require(process.env.VAULTFLOW_PLAYWRIGHT_PACKAGE || 'playwright');
const origin = process.argv[2] || 'http://localhost:5321';

(async () => {
    const browser = await chromium.launch({ headless: true });
    try {
        const context = await browser.newContext({ ...devices['Pixel 5'], serviceWorkers: 'block' });
        const page = await context.newPage();
        const pageErrors = [];
        page.on('console', entry => { if (entry.type() === 'error') pageErrors.push(entry.text()); });
        page.on('pageerror', error => pageErrors.push(error.message));
        let releaseDownload;
        let downloadStarted;
        const downloadObserved = new Promise(resolve => { downloadStarted = resolve; });
        const downloadGate = new Promise(resolve => { releaseDownload = resolve; });
        const runtimeScript = /\/offline\/_framework\/dotnet[^/]*\.js(?:\?.*)?$/;
        await page.route(runtimeScript, async route => {
            downloadStarted();
            await downloadGate;
            await route.continue();
        });
        await page.goto(`${origin}/offline/`, { waitUntil: 'domcontentloaded' });
        await downloadObserved;
        await page.getByRole('heading', { name: 'Opening your workspace' }).waitFor();
        if (await page.locator('#blazor-error-ui').isVisible())
            throw new Error('The error banner was visible during a normal download.');
        await page.getByRole('link', { name: 'Open VaultFlow online' }).waitFor();
        releaseDownload();
        await page.getByRole('heading', { name: 'Welcome back' }).waitFor().catch(async error => {
            console.error('Startup page:', (await page.locator('body').innerText()).slice(0, 1000));
            console.error('Browser errors:', pageErrors.slice(0, 5));
            throw error;
        });
        await context.close();

        const failedContext = await browser.newContext({ ...devices['Pixel 5'], serviceWorkers: 'block' });
        const failedPage = await failedContext.newPage();
        await failedPage.route(runtimeScript, route => route.abort());
        await failedPage.goto(`${origin}/offline/`, { waitUntil: 'domcontentloaded' });
        await failedPage.getByRole('heading', { name: 'The app could not finish loading' }).waitFor();
        await failedPage.getByRole('button', { name: 'Try again' }).waitFor();
        await failedPage.getByText('If browser data was cleared, offline files and work saved only on this device may have been removed.', { exact: false }).waitFor();
        await failedContext.close();
        console.log('Phone startup passed: visible loading UI, successful delayed download, and readable download failure.');
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
