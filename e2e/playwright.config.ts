import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './tests',
  timeout: 240_000,
  fullyParallel: false,
  reporter: [['list']],
  use: {
    baseURL: process.env.CRM_BASE_URL || 'http://localhost:5025',
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
  },
});
