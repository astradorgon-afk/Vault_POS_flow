const { chromium, devices } = require(process.env.VAULTFLOW_PLAYWRIGHT_PACKAGE || 'playwright');
const origin = process.argv[2] || 'http://localhost:5321';

(async () => {
    const browser = await chromium.launch({ headless: true });
    try {
        const context = await browser.newContext({ ...devices['Pixel 5'], serviceWorkers: 'block' });
        const page = await context.newPage();
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
        await page.getByRole('heading', { name: 'Opening your offline app' }).waitFor();
        if (await page.locator('#blazor-error-ui').isVisible())
            throw new Error('The error banner was visible during a normal download.');
        await page.getByRole('link', { name: 'Open the online workspace' }).waitFor();
        releaseDownload();
        await page.getByRole('heading', { name: 'Install VaultFlow Offline' }).waitFor();
        await context.close();

        const failedContext = await browser.newContext({ ...devices['Pixel 5'], serviceWorkers: 'block' });
        const failedPage = await failedContext.newPage();
        await failedPage.route(runtimeScript, route => route.abort());
        await failedPage.goto(`${origin}/offline/`, { waitUntil: 'domcontentloaded' });
        await failedPage.getByRole('heading', { name: 'The app could not finish loading' }).waitFor();
        await failedPage.getByRole('button', { name: 'Try again' }).waitFor();
        await failedContext.close();
        console.log('Phone startup passed: visible loading UI, successful delayed download, and readable download failure.');
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
