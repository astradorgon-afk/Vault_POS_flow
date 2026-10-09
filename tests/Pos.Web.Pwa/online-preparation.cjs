const { chromium } = require(process.env.VAULTFLOW_PLAYWRIGHT_PACKAGE || 'playwright');

const origin = process.argv[2] || 'http://127.0.0.1:5436';
(async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    const context = await browser.newContext();
    const page = await context.newPage();
    const errors = [];
    const failedRequests = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('requestfailed', request => failedRequests.push(`${request.url()}: ${request.failure()?.errorText}`));
    await page.goto(origin + '/login');
    await page.getByRole('heading', { name: 'Welcome back' }).waitFor();
    await page.waitForTimeout(1000);
    if (await page.getByLabel('Installation enrollment code').count())
      throw new Error('Enrollment code appeared before a successful login.');
    await page.getByLabel('Username or email').fill('pilot');
    await page.getByLabel('Password', { exact: true }).fill('account-password');
    await page.getByRole('button', { name: 'Choose work location' }).click();
    await page.getByLabel(/PILOT Pilot store/).check();
    await page.getByRole('button', { name: 'Open workspace' }).click();
    const setup = page.locator('.offline-setup-dialog');
    await setup.waitFor();
    await setup.getByRole('heading', { name: 'Set up offline access' }).waitFor();
    await setup.getByText('You can use your online workspace now.', { exact: false }).waitFor();
    await setup.getByRole('button', { name: 'Continue online without enrolling' }).waitFor();
    await setup.getByLabel('Installation enrollment code').fill('pilot-enrolment');
    await setup.getByRole('button', { name: 'Enroll and prepare offline access' }).click();
    await setup.getByRole('heading', { name: 'Offline access is ready' }).waitFor({ timeout: 60000 });
    const installButton = setup.getByRole('button', { name: 'Install VaultFlow' });
    await installButton.waitFor();
    await page.evaluate(() => {
      window.__installPromptCalls = 0;
      const event = new Event('beforeinstallprompt', { cancelable: true });
      event.prompt = async () => { window.__installPromptCalls++; return { outcome: 'accepted' }; };
      window.dispatchEvent(event);
    });
    await installButton.click();
    await setup.getByText('Installation accepted.', { exact: false }).waitFor();
    if (await page.evaluate(() => window.__installPromptCalls) !== 1)
      throw new Error('Install button did not call the browser installation prompt.');
    await page.evaluate(() => window.dispatchEvent(new Event('appinstalled')));
    await setup.getByText('VaultFlow is installed on this device.').waitFor();
    const state = await page.evaluate(async () => {
      const store = await import('/offline/js/offline-store.js');
      return store.initialize();
    });
    if (!state.device || state.profiles.length !== 1 || state.profiles[0].userName !== 'pilot')
      throw new Error(`Sign-in did not prepare a local account: ${JSON.stringify(state)}`);
    await setup.getByRole('button', { name: 'Continue to workspace' }).click();
    await setup.waitFor({ state: 'hidden' });
    await context.setOffline(true);
    await page.goto(origin + '/login');
    await page.getByRole('heading', { name: 'Welcome back' }).waitFor();
    await page.waitForTimeout(1000);
    await page.getByText('Opening saved data').waitFor({ state: 'hidden' });
    await page.getByLabel('Username or email').fill('pilot');
    await page.getByLabel('Password', { exact: true }).fill('account-password');
    await page.getByRole('button', { name: 'Open workspace' }).click();
    await page.getByRole('heading', { name: 'Welcome, Pilot User' }).waitFor();
    await page.evaluate(() => localStorage.removeItem('vaultflow-app-installed'));
    await context.setOffline(false);
    await page.goto(origin + '/login');
    await page.getByRole('heading', { name: 'Welcome back' }).waitFor();
    await page.waitForTimeout(1000);
    if (await page.getByLabel('Installation enrollment code').count())
      throw new Error('Enrollment was requested again for an enrolled installation.');
    await page.getByLabel('Username or email').fill('pilot');
    await page.getByLabel('Password', { exact: true }).fill('account-password');
    await page.getByRole('button', { name: 'Choose work location' }).click();
    await page.getByLabel(/PILOT Pilot store/).check();
    await page.getByRole('button', { name: 'Open workspace' }).click();
    try {
      await page.getByRole('dialog', { name: 'Offline access is ready' })
        .getByRole('button', { name: 'Install VaultFlow' }).waitFor({ timeout: 60000 });
    } catch (error) {
      throw new Error(`${error.message}; page=${(await page.locator('body').innerText()).slice(-900)}; requests=${failedRequests.join('; ')}`);
    }
    if (await page.getByLabel('Installation enrollment code').count())
      throw new Error('Previously enrolled device was asked for a second enrollment code.');
    await page.getByRole('dialog', { name: 'Offline access is ready' })
      .getByRole('button', { name: 'Continue to workspace' }).click();
    if (errors.length) throw new Error(errors.join('; '));
    const onlineOnlyContext = await browser.newContext();
    await onlineOnlyContext.route('**/_content/Pos.SharedUI/login.css', route => route.abort());
    const onlineOnly = await onlineOnlyContext.newPage();
    await onlineOnly.goto(origin + '/login');
    await onlineOnly.waitForTimeout(1000);
    await onlineOnly.getByLabel('Username or email').fill('pilot');
    await onlineOnly.getByLabel('Password', { exact: true }).fill('account-password');
    await onlineOnly.getByRole('button', { name: 'Choose work location' }).click();
    await onlineOnly.getByLabel(/PILOT Pilot store/).check();
    await onlineOnly.getByRole('button', { name: 'Open workspace' }).click();
    const overlay = onlineOnly.locator('.offline-setup-backdrop');
    await overlay.waitFor();
    const overlayPosition = await overlay.evaluate(element => {
      const bounds = element.getBoundingClientRect();
      return { position: getComputedStyle(element).position,
        coversViewport: bounds.top === 0 && bounds.left === 0 &&
          bounds.width === innerWidth && bounds.height === innerHeight };
    });
    if (overlayPosition.position !== 'fixed' || !overlayPosition.coversViewport)
      throw new Error(`Offline prompt fell below the login form without the shared stylesheet: ${JSON.stringify(overlayPosition)}`);
    await onlineOnly.getByRole('dialog', { name: 'Set up offline access' })
      .getByRole('button', { name: 'Continue online without enrolling' }).click();
    await onlineOnly.waitForURL(origin + '/');
    await onlineOnlyContext.close();
    console.log('Online preparation passed: enrollment required once, account unlocked offline, later sign-in refreshed the saved profile.');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
