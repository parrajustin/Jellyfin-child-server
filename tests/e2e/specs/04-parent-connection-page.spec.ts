/**
 * 04 - the parent server settings page in the child's dashboard.
 *
 * Golden screenshots cover the page as loaded with the saved settings and the three
 * outcomes of "Test connection": wrong password, unreachable parent, success.
 * Preconditions: lib/global-setup.ts connected the child to the parent through the gate.
 */
import { expect, test, type Locator, type Page } from '@playwright/test';
import { CREDENTIALS, SERVER_NAMES, env } from '../lib/env';
import { login, selectors, stabilize } from '../lib/ui';

test.describe.configure({ mode: 'serial' });
// The dashboard page scrolls inside its own container; a tall viewport lets one element
// screenshot show the whole page including the outcome message and the status block.
test.use({ viewport: { width: 1280, height: 1800 } });

const PAGE_NAME = 'ChildServerParent';
const PAGE_ROUTE = `${env.childUrl}/web/#/configurationpage?name=${PAGE_NAME}`;
const OUTCOME = /^(Success|InvalidCredentials|AccessDenied|ConnectionFailed|InvalidResponse|NotConfigured|RequestFailed)$/;

async function openSettingsPage(page: Page): Promise<Locator> {
  await page.goto(PAGE_ROUTE, { waitUntil: 'domcontentloaded' });
  const root = page.locator('#childServerParentPage').last();
  await root.waitFor({ state: 'visible', timeout: 60_000 });
  // The script fills the form from GET /ChildServer/Configuration and the status from GET /ChildServer/Status.
  await expect(root.locator('#txtParentUrl')).toHaveValue(/.+/, { timeout: 30_000 });
  await expect(root.locator('#factConfigured .csBadge')).toBeVisible({ timeout: 30_000 });
  await stabilize(page);
  return root;
}

async function testConnection(root: Locator, url: string, username: string, password: string): Promise<Locator> {
  await root.locator('#txtParentUrl').fill(url);
  await root.locator('#txtParentUsername').fill(username);
  await root.locator('#txtParentPassword').fill(password);
  await root.locator('#btnTestConnection').click();
  const box = root.locator('#connectionStatus');
  await expect(box).toHaveAttribute('data-status', OUTCOME, { timeout: 60_000 });
  return box;
}

const screenshotOptions = (root: Locator) => ({ mask: [root.locator('.childServerTime')] });

test.beforeEach(async ({ page }) => {
  await login(page, CREDENTIALS.childAdmin.username, CREDENTIALS.childAdmin.password);
});

test('the dashboard menu links to the parent server page', async ({ page }) => {
  await page.goto(`${env.childUrl}/web/#/dashboard`, { waitUntil: 'domcontentloaded' });
  const link = page.locator(`a[href="#/configurationpage?name=${PAGE_NAME}"]`).first();
  await expect(link).toBeVisible({ timeout: 60_000 });
  await expect(link).toContainText('Parent server');
});

test('the page loads the saved settings without the password', async ({ page }) => {
  const root = await openSettingsPage(page);

  await expect(root.locator('#txtParentUrl')).toHaveValue(`${env.parentUrlForChild}/`);
  await expect(root.locator('#txtParentUsername')).toHaveValue(CREDENTIALS.childToParent.username);
  await expect(root.locator('#txtParentPassword')).toHaveValue('');
  await expect(root.locator('#passwordHelp')).toContainText('A password is stored');
  await expect(root.locator('#factConfigured .csBadge')).toHaveText('Yes');
  await expect(root.locator('#factSignedIn .csBadge')).toHaveText('Yes');
  await expect(root.locator('#factParent')).toContainText(SERVER_NAMES.parent);
  await expect(root.locator('#connectionStatus')).toBeHidden();

  await expect(root).toHaveScreenshot('parent-page-loaded.png', screenshotOptions(root));
});

test('an incorrect password is reported', async ({ page }) => {
  const root = await openSettingsPage(page);

  const box = await testConnection(root, env.parentUrlForChild, CREDENTIALS.childToParent.username, 'definitely-wrong');

  await expect(box).toHaveAttribute('data-status', 'InvalidCredentials');
  await expect(box.locator('#connectionStatusTitle')).toHaveText('Incorrect user name or password');
  await expect(box.locator('#connectionStatusDetail')).toContainText('rejected the user name or password');
  await expect(root).toHaveScreenshot('parent-page-invalid-password.png', screenshotOptions(root));
});

test('an unreachable parent is reported', async ({ page }) => {
  const root = await openSettingsPage(page);

  const box = await testConnection(root, 'http://gate:1', CREDENTIALS.childToParent.username, CREDENTIALS.childToParent.password);

  await expect(box).toHaveAttribute('data-status', 'ConnectionFailed');
  await expect(box.locator('#connectionStatusTitle')).toHaveText('Could not connect to the parent server');
  await expect(box.locator('#connectionStatusDetail')).toContainText('Could not connect to gate');
  await expect(root).toHaveScreenshot('parent-page-unreachable.png', screenshotOptions(root));
});

test('a correct sign in is reported as success', async ({ page }) => {
  const root = await openSettingsPage(page);

  const box = await testConnection(root, env.parentUrlForChild, CREDENTIALS.childToParent.username, CREDENTIALS.childToParent.password);

  await expect(box).toHaveAttribute('data-status', 'Success');
  await expect(box.locator('#connectionStatusTitle')).toHaveText('Connected');
  await expect(box.locator('#connectionStatusDetail')).toContainText(SERVER_NAMES.parent);
  await expect(box.locator('#connectionStatusDetail')).toContainText(`as ${CREDENTIALS.childToParent.username}`);
  await expect(root).toHaveScreenshot('parent-page-success.png', screenshotOptions(root));
});

test('saving signs in with the stored password and confirms with a toast', async ({ page }) => {
  const root = await openSettingsPage(page);

  // Leave the password empty: the stored one must be kept.
  await root.locator('#txtParentPassword').fill('');
  await root.locator('#btnSave').click();

  const box = root.locator('#connectionStatus');
  await expect(box).toHaveAttribute('data-status', 'Success', { timeout: 60_000 });
  await expect(page.locator(selectors.toast).last()).toContainText('Settings saved', { timeout: 15_000 });
  await expect(root.locator('#factSignedIn .csBadge')).toHaveText('Yes');
});
