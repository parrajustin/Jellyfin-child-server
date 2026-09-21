import { defineConfig, devices } from '@playwright/test';
import { env } from './lib/env';

const VIEWPORT = { width: 1280, height: 800 };

export default defineConfig({
  testDir: './specs',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 180_000,
  expect: {
    timeout: 20_000,
    toHaveScreenshot: {
      maxDiffPixelRatio: 0.02,
      animations: 'disabled',
      caret: 'hide',
      scale: 'css',
    },
  },
  // No platform or project suffix on purpose: goldens are only ever produced inside the
  // Linux e2e container (see README, "Golden screenshots").
  snapshotPathTemplate: '{testDir}/__screenshots__/{testFileName}/{arg}{ext}',
  // A developer without goldens can still run the flows with E2E_IGNORE_SNAPSHOTS=1.
  ignoreSnapshots: process.env.E2E_IGNORE_SNAPSHOTS === '1',
  globalSetup: './lib/global-setup.ts',
  outputDir: 'test-results/artifacts',
  reporter: [
    ['list'],
    ['html', { outputFolder: 'test-results/html', open: 'never' }],
    ['junit', { outputFile: 'test-results/junit.xml' }],
  ],
  use: {
    baseURL: env.childUrl,
    viewport: VIEWPORT,
    colorScheme: 'dark',
    locale: 'en-US',
    timezoneId: 'UTC',
    screenshot: 'only-on-failure',
    trace: 'retain-on-failure',
    video: 'off',
    deviceScaleFactor: 1,
  },
  projects: [
    {
      name: 'chromium',
      // The device descriptor carries its own viewport (1280x720); ours wins.
      use: { ...devices['Desktop Chrome'], viewport: VIEWPORT, deviceScaleFactor: 1 },
    },
  ],
});
