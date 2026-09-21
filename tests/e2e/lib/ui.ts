/**
 * Playwright helpers for jellyfin-web 12.1 as served by the child server.
 * Selectors verified against jellyfin-web v12.1 (src/apps/legacy/controllers/...).
 */
import { expect, type Locator, type Page } from '@playwright/test';
import { env } from './env';

export const routes = {
  login: (): string => `${env.childUrl}/web/#/login`,
  home: (): string => `${env.childUrl}/web/#/home`,
  dashboard: (): string => `${env.childUrl}/web/#/dashboard`,
  /** serverId is the child's GET /System/Info/Public Id. */
  details: (itemId: string, serverId: string): string =>
    `${env.childUrl}/web/#/details?id=${encodeURIComponent(itemId)}&serverId=${encodeURIComponent(serverId)}`,
} as const;

export const selectors = {
  loginPage: '#loginPage',
  manualLoginForm: 'form.manualLoginForm',
  btnManual: '.btnManual',
  username: '#txtManualName',
  password: '#txtManualPassword',
  loginSubmit: 'form.manualLoginForm button.button-submit',
  homePage: '#indexPage',
  homeSections: '#indexPage .sections',
  detailPage: '#itemDetailPage',
  playButton: '#itemDetailPage .btnPlay:not(.hide)',
  video: 'video.htmlvideoplayer',
  osdPage: '#videoOsdPage',
  osdBottom: '#videoOsdPage .videoOsdBottom',
  dialog: '.dialogContainer .dialog.opened',
  dialogTitle: '.formDialogHeaderTitle',
  dialogText: '.formDialogContent .text',
  dialogButton: '.formDialogFooter button.btnOption',
  toast: '.toastContainer .toast',
} as const;

const STABILIZE_CSS = `
*, *::before, *::after {
  animation: none !important;
  transition: none !important;
  caret-color: transparent !important;
  scroll-behavior: auto !important;
}
`;

/** Hidden while capturing the player: the video frame and the wall-clock "Ends at" text. */
const PLAYER_SCREENSHOT_CSS = `
video.htmlvideoplayer { visibility: hidden !important; }
.endsAtText { visibility: hidden !important; }
`;

const HOME_HASH = /^#\/home(\.html)?(\?.*)?$/;

/** Disables CSS animations and waits for web fonts before a screenshot. */
export async function stabilize(page: Page): Promise<void> {
  await page.addStyleTag({ content: STABILIZE_CSS });
  await page.evaluate(() => document.fonts.ready.then(() => undefined));
  await page.waitForLoadState('networkidle', { timeout: 10_000 }).catch(() => undefined);
}

/** Signs in through the login page and waits for the home route. */
export async function login(page: Page, username: string, password: string): Promise<void> {
  await page.goto(routes.login(), { waitUntil: 'domcontentloaded' });
  await page.locator(selectors.loginPage).waitFor({ state: 'visible', timeout: 60_000 });

  // jellyfin-web shows either the user tiles (manual form hidden behind .btnManual) or the
  // manual form itself when there are no public users. Wait for whichever appears.
  await page
    .locator(`${selectors.manualLoginForm}:not(.hide):visible, ${selectors.btnManual}:visible`)
    .first()
    .waitFor({ state: 'visible', timeout: 60_000 });

  const form = page.locator(selectors.manualLoginForm);
  const formHidden = await form.evaluate((element) => element.classList.contains('hide'));
  if (formHidden) {
    await page.locator(selectors.btnManual).first().click();
    await form.waitFor({ state: 'visible' });
  }

  await page.locator(selectors.username).fill(username);
  await page.locator(selectors.password).fill(password);
  await page.locator(selectors.loginSubmit).click();

  await page.waitForURL((url) => HOME_HASH.test(url.hash), { timeout: 60_000 });
  await expect(page.locator(selectors.loginPage)).toBeHidden({ timeout: 30_000 });
  expect(page.url()).not.toContain('#/login');
}

/** Waits for the home page sections to be rendered. */
export async function waitForHome(page: Page): Promise<void> {
  await page.locator(selectors.homePage).waitFor({ state: 'visible', timeout: 60_000 });
  await expect(page.locator(selectors.homeSections).locator('*').first()).toBeAttached({ timeout: 60_000 });
}

/** Opens an item detail page and waits for its play button. */
export async function openDetails(page: Page, itemId: string, serverId: string): Promise<void> {
  await page.goto(routes.details(itemId, serverId), { waitUntil: 'domcontentloaded' });
  await page.locator(selectors.detailPage).waitFor({ state: 'visible', timeout: 60_000 });
  await page.locator(selectors.playButton).first().waitFor({ state: 'visible', timeout: 60_000 });
}

export async function clickPlay(page: Page): Promise<void> {
  await page.locator(selectors.playButton).first().click();
}

export interface DialogText {
  title: string;
  text: string;
}

/** Reads the currently opened dialog, if any. */
export async function readDialog(page: Page): Promise<DialogText | null> {
  const dialog = page.locator(selectors.dialog).first();
  if (!(await dialog.isVisible().catch(() => false))) {
    return null;
  }
  const title = (await dialog.locator(selectors.dialogTitle).first().textContent().catch(() => '')) ?? '';
  const text = (await dialog.locator(selectors.dialogText).first().textContent().catch(() => '')) ?? '';
  return { title: title.trim(), text: text.trim() };
}

export async function dismissDialog(page: Page): Promise<void> {
  const button = page.locator(selectors.dialog).first().locator(selectors.dialogButton).first();
  await button.click();
}

export function toast(page: Page): Locator {
  return page.locator(selectors.toast);
}

/**
 * Waits until the HTML5 player is actually playing (readyState >= 2, not paused,
 * currentTime > 0). Fails early with the dialog text if jellyfin-web shows an error dialog.
 */
export async function waitForPlayback(page: Page, timeoutMs: number = 90_000): Promise<void> {
  const playing = page
    .waitForFunction(
      (selector) => {
        const video = document.querySelector<HTMLVideoElement>(selector);
        return !!video && video.readyState >= 2 && !video.paused && video.currentTime > 0;
      },
      selectors.video,
      { timeout: timeoutMs, polling: 250 },
    )
    .then(() => 'playing' as const);

  const dialogShown = page
    .locator(selectors.dialog)
    .first()
    .waitFor({ state: 'visible', timeout: timeoutMs })
    .then(
      () => 'dialog' as const,
      () => new Promise<never>(() => undefined), // let the playback wait decide on timeout
    );

  const outcome = await Promise.race([playing, dialogShown]);
  if (outcome === 'dialog') {
    const dialog = await readDialog(page);
    throw new Error(`playback did not start: dialog "${dialog?.title ?? ''}": ${dialog?.text ?? ''}`);
  }
}

/** Hides the (non-deterministic) video frame and wall-clock text while capturing the player. */
export async function hideVideoFrame(page: Page): Promise<void> {
  await page.addStyleTag({ content: PLAYER_SCREENSHOT_CSS });
}

/**
 * jellyfin-web hides the OSD 3 s after the last pointer input, playing or paused. Real
 * mouse moves bring it back; a page-side keep-alive then re-triggers pointer moves every
 * second so the OSD stays visible while the screenshot is compared.
 */
export async function showPlayerOsd(page: Page): Promise<void> {
  await page.mouse.move(600, 300);
  await page.mouse.move(700, 360);
  await expect(page.locator(selectors.osdBottom).first()).toBeVisible({ timeout: 10_000 });
  await expect(page.locator(selectors.osdBottom).first()).not.toHaveClass(/videoOsdBottom-hidden/, {
    timeout: 10_000,
  });
  await page.evaluate(() => {
    const w = window as unknown as { __e2eOsdKeepAlive?: number };
    if (w.__e2eOsdKeepAlive) {
      return;
    }
    let flip = false;
    w.__e2eOsdKeepAlive = window.setInterval(() => {
      flip = !flip;
      // jellyfin-web ignores small pointer jitter; alternate between two distant points.
      const x = flip ? 700 : 600;
      const y = flip ? 360 : 300;
      document.dispatchEvent(
        new PointerEvent('pointermove', { bubbles: true, clientX: x, clientY: y, pointerType: 'mouse', pointerId: 1 }),
      );
      document.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, clientX: x, clientY: y }));
    }, 1_000);
  });
}

export async function stopOsdKeepAlive(page: Page): Promise<void> {
  await page
    .evaluate(() => {
      const w = window as unknown as { __e2eOsdKeepAlive?: number };
      if (w.__e2eOsdKeepAlive) {
        window.clearInterval(w.__e2eOsdKeepAlive);
        delete w.__e2eOsdKeepAlive;
      }
    })
    .catch(() => undefined);
}
