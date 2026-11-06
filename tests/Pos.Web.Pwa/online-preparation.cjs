const { chromium } = require(process.env.VAULTFLOW_PLAYWRIGHT_PACKAGE || 'playwright');

const origin = process.argv[2] || 'http://127.0.0.1:5436';

// Work location sits under "Additional sign-in options" as a select. A user with
// one assigned location gets it automatically; choosing it explicitly still works.
async function chooseLocation(page) {
  await page.getByRole('button', { name: 'Additional sign-in options' }).click();
  const select = page.getByLabel('Work location');
  await select.locator('option', { hasText: 'PILOT' }).first().waitFor({ state: 'attached' });
  const value = await select.locator('option', { hasText: 'PILOT' }).first().getAttribute('value');
  await select.selectOption(value);
}
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
    if (await page.getByLabel(/enrollment code/i).count())
      throw new Error('Enrollment code appeared before the sign-in options were opened.');
    await page.getByLabel('Username or email').fill('pilot');
    await page.getByLabel('Password', { exact: true }).fill('account-password');
    await chooseLocation(page);
    // Enrolling is opt-in: the code goes in with the other sign-in options.
    await page.getByLabel('Offline enrollment code').fill('pilot-enrolment');
    await page.getByRole('button', { name: 'Open workspace' }).click();
    await page.waitForURL(origin + '/', { timeout: 60000 });
    const banner = page.locator('.offline-ready-banner');
    await banner.waitFor({ timeout: 60000 });
    await banner.getByText('Offline access is ready on this device.').waitFor();
    const installButton = banner.getByRole('button', { name: 'Install VaultFlow' });
    await installButton.waitFor();
    await page.evaluate(() => {
      window.__installPromptCalls = 0;
      const event = new Event('beforeinstallprompt', { cancelable: true });
      event.prompt = async () => { window.__installPromptCalls++; return { outcome: 'accepted' }; };
      window.dispatchEvent(event);
    });
    await installButton.click();
    await banner.getByText('Installation accepted.', { exact: false }).waitFor();
    if (await page.evaluate(() => window.__installPromptCalls) !== 1)
      throw new Error('Install button did not call the browser installation prompt.');
    await page.evaluate(() => window.dispatchEvent(new Event('appinstalled')));
    await banner.getByText('VaultFlow is installed on this device.').waitFor();
    const state = await page.evaluate(async () => {
      const store = await import('/offline/js/offline-store.js');
      return store.initialize();
    });
    if (!state.device || state.profiles.length !== 1 || state.profiles[0].userName !== 'pilot')
      throw new Error(`Sign-in did not prepare a local account: ${JSON.stringify(state)}`);
    await banner.getByRole('button', { name: 'Dismiss offline notice' }).click();
    await banner.waitFor({ state: 'hidden' });
    await context.setOffline(true);
    await page.goto(origin + '/login');
    await page.getByRole('heading', { name: 'Welcome back' }).waitFor();
    await page.waitForTimeout(1000);
    await page.getByText('Opening saved data').waitFor({ state: 'hidden' });
    await page.getByLabel('Username or email').fill('pilot');
    await page.getByLabel('Password', { exact: true }).fill('account-password');
    await page.getByRole('button', { name: 'Open workspace' }).click();
    await page.getByRole('heading', { name: 'Welcome back, Pilot.' }).waitFor();
    await page.evaluate(() => localStorage.removeItem('vaultflow-app-installed'));
    await context.setOffline(false);
    await page.goto(origin + '/login');
    await page.getByRole('heading', { name: 'Welcome back' }).waitFor();
    await page.waitForTimeout(1000);
    await page.getByLabel('Username or email').fill('pilot');
    await page.getByLabel('Password', { exact: true }).fill('account-password');
    await chooseLocation(page);
    if (await page.getByLabel(/enrollment code/i).count())
      throw new Error('Enrollment was requested again for an enrolled installation.');
    await page.getByRole('button', { name: 'Open workspace' }).click();
    const secondBanner = page.locator('.offline-ready-banner');
    try {
      await secondBanner.getByRole('button', { name: 'Install VaultFlow' }).waitFor({ timeout: 60000 });
    } catch (error) {
      throw new Error(`${error.message}; page=${(await page.locator('body').innerText()).slice(-900)}; requests=${failedRequests.join('; ')}`);
    }
    if (await page.getByLabel(/enrollment code/i).count())
      throw new Error('Previously enrolled device was asked for a second enrollment code.');
    await secondBanner.getByRole('button', { name: 'Dismiss offline notice' }).click();
    if (errors.length) throw new Error(errors.join('; '));
    // An ordinary browser tab that is not enrolled goes straight to the
    // workspace: nobody there is waiting to enrol, so no dialog interrupts.
    const onlineOnlyContext = await browser.newContext();
    const onlineOnly = await onlineOnlyContext.newPage();
    await onlineOnly.goto(origin + '/login');
    await onlineOnly.waitForTimeout(1000);
    await onlineOnly.getByLabel('Username or email').fill('pilot');
    await onlineOnly.getByLabel('Password', { exact: true }).fill('account-password');
    await chooseLocation(onlineOnly);
    await onlineOnly.getByRole('button', { name: 'Open workspace' }).click();
    await onlineOnly.waitForURL(origin + '/');
    if (await onlineOnly.locator('.offline-setup-backdrop').count())
      throw new Error('A browser tab was interrupted by the offline setup dialog.');
    await onlineOnlyContext.close();

    // The installed app still offers enrollment, even without the shared stylesheet.
    const installedContext = await browser.newContext();
    await installedContext.addInitScript(() => localStorage.setItem('vaultflow-app-installed', 'true'));
    await installedContext.route('**/_content/Pos.SharedUI/login.css', route => route.abort());
    const installedApp = await installedContext.newPage();
    await installedApp.goto(origin + '/login');
    await installedApp.waitForTimeout(1000);
    await installedApp.getByLabel('Username or email').fill('pilot');
    await installedApp.getByLabel('Password', { exact: true }).fill('account-password');
    await chooseLocation(installedApp);
    await installedApp.getByRole('button', { name: 'Open workspace' }).click();
    const overlay = installedApp.locator('.offline-setup-backdrop');
    await overlay.waitFor();
    const overlayPosition = await overlay.evaluate(element => {
      const bounds = element.getBoundingClientRect();
      return { position: getComputedStyle(element).position,
        coversViewport: bounds.top === 0 && bounds.left === 0 &&
          bounds.width === innerWidth && bounds.height === innerHeight };
    });
    if (overlayPosition.position !== 'fixed' || !overlayPosition.coversViewport)
      throw new Error(`Offline prompt fell below the login form without the shared stylesheet: ${JSON.stringify(overlayPosition)}`);
    await installedApp.getByRole('dialog', { name: 'Set up offline access' })
      .getByRole('button', { name: 'Continue online without enrolling' }).click();
    await installedApp.waitForURL(origin + '/');
    await installedContext.close();
    console.log('Online preparation passed: opt-in enrollment, account unlocked offline, later sign-in refreshed the saved profile, browser tabs not interrupted, installed app still prompted.');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
