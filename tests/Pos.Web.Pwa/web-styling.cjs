const { chromium, devices } = require(process.env.VAULTFLOW_PLAYWRIGHT_PACKAGE || 'playwright');
const origin = process.argv[2] || 'http://localhost:5215';

(async () => {
    const browser = await chromium.launch({ headless: true,
        executablePath: process.env.VAULTFLOW_BROWSER_EXECUTABLE || undefined });
    try {
        const context = await browser.newContext({ ...devices['Pixel 5'] });
        const page = await context.newPage();
        const externalFonts = [];
        page.on('request', request => {
            if (/fonts\.(googleapis|gstatic)\.com/.test(request.url())) externalFonts.push(request.url());
        });
        await context.route(/https:\/\/fonts\.(googleapis|gstatic)\.com\//, route => route.abort());
        await page.goto(`${origin}/login`, { waitUntil: 'domcontentloaded' });
        if (origin.includes('ngrok')) {
            try {
                await page.getByRole('button', { name: 'Visit Site', exact: true }).waitFor({ timeout: 10000 });
                await page.getByRole('button', { name: 'Visit Site', exact: true }).click();
            } catch { /* The visitor notice may have already been accepted. */ }
        }
        await page.getByRole('heading', { name: 'Welcome back' }).waitFor();
        await page.waitForFunction(() =>
            getComputedStyle(document.querySelector('.login-story')).backgroundColor === 'rgb(21, 60, 43)');
        await page.evaluate(() => document.fonts.ready);
        const styling = await page.evaluate(() => ({
            grid: getComputedStyle(document.querySelector('.login-page')).display,
            fonts: document.fonts.check('16px "DM Sans"') && document.fonts.check('16px "Manrope"'),
            staleTags: Array.from(document.querySelectorAll('link[rel="stylesheet"]')).some(link => link.href.includes('?v=')),
        }));
        if (styling.grid !== 'grid' || !styling.fonts || styling.staleTags || externalFonts.length)
            throw new Error(`Phone stylesheet check failed: ${JSON.stringify({ styling, externalFonts })}`);
        console.log('Phone website styling passed: layout, local fonts, and fresh stylesheet URLs with external fonts blocked.');
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
