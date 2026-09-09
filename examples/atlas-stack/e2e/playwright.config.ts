import { defineConfig } from '@playwright/test';

// Requires Node 18+. Run: npm i && npx playwright install chromium && npm test
export default defineConfig({
  testDir: '.',
  timeout: 180_000,
  expect: { timeout: 30_000 },
  retries: 0,
  workers: 1, // serial: uploads mutate shared server state
  reporter: [['list']],
  use: {
    baseURL: process.env.ATLAS_BASE_URL || 'https://atlas.example',
    headless: process.env.HEADED ? false : true,
    actionTimeout: 25_000,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
});
