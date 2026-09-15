import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 60_000,
  outputDir: '../../artifacts/0025/test-results',
  use: { browserName: 'chromium', viewport: { width: 1280, height: 1000 }, trace: 'retain-on-failure' },
  reporter: [['list'], ['html', { open: 'never', outputFolder: '../../artifacts/0025/playwright-report' }]],
});
