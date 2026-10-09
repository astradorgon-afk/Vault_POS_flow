const { chromium } = require(process.env.VAULTFLOW_PLAYWRIGHT_PACKAGE || 'playwright');

const origin = process.argv[2] || 'http://127.0.0.1:5432';
(async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    const context = await browser.newContext();
    const page = await context.newPage();
    const errors = [];
    const interactive = new Promise(resolve => page.on('console', message => {
      if (message.text().includes('WebSocket connected to')) resolve();
    }));
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => { if (message.type() === 'warning' || message.type() === 'error') errors.push(message.text()); });
    page.on('requestfailed', request => errors.push(`${request.url()}: ${request.failure()?.errorText}`));
    await page.goto(origin + '/login');
    if (origin.includes('ngrok-free.')) {
      await page.getByText('Visit Site', { exact: true }).waitFor();
      await page.getByText('Visit Site', { exact: true }).click();
    }
    await page.getByRole('heading', { name: 'Welcome back' }).waitFor();
    await Promise.race([interactive, new Promise((_, reject) =>
      setTimeout(() => reject(new Error('Interactive login did not connect.')), 20000))]);
    const otpToggle = page.getByRole('button', { name: 'Have a two-factor code?' });
    await otpToggle.waitFor();
    if (await page.getByLabel('Two-factor code').count()) throw new Error('Two-factor field was open by default.');
    for (let attempt = 0; attempt < 10 && !await page.getByLabel('Two-factor code').count(); attempt++) {
      await otpToggle.click();
      await page.waitForTimeout(500);
    }
    await page.getByLabel('Two-factor code').waitFor();
    await otpToggle.click();
    await page.getByLabel('Two-factor code').waitFor({ state: 'detached' });
    try {
      await page.evaluate(async () => Promise.race([
        navigator.serviceWorker.ready,
        new Promise((_, reject) => setTimeout(() => reject(new Error('Root worker did not activate.')), 20000)),
      ]));
    } catch (error) {
      const registrations = await page.evaluate(async () =>
        (await navigator.serviceWorker.getRegistrations()).map(registration => ({
          scope: registration.scope, installing: registration.installing?.state,
          waiting: registration.waiting?.state, active: registration.active?.state,
        })));
      const state = await page.evaluate(() => ({ secure: isSecureContext,
        serviceWorker: 'serviceWorker' in navigator,
        rootScript: !!document.querySelector('script[src="/js/root-pwa-register.js"]'),
        page: location.href }));
      throw new Error(`${error.message}; state=${JSON.stringify(state)}; registrations=${JSON.stringify(registrations)}; pageErrors=${JSON.stringify(errors)}`);
    }
    await page.waitForFunction(async () => {
      const keys = await caches.keys();
      return keys.some(key => key.startsWith('vaultflow-root-shell-'));
    });
    await context.setOffline(true);
    await page.goto(origin + '/login');
    await page.getByRole('heading', { name: 'Welcome back' }).waitFor();
    if (new URL(page.url()).pathname !== '/login') throw new Error('Login URL changed offline.');
    if (await page.getByLabel('Two-factor code').count()) throw new Error('Two-factor field remained offline.');
    await page.getByLabel('Enroll installation').waitFor();
    await page.getByLabel('Username or email').waitFor();
    await page.getByLabel('Password', { exact: true }).waitFor();
    if (errors.length) throw new Error(errors.join('; '));
    console.log('Unified login passed: same /login URL, cached offline client, no offline OTP.');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
