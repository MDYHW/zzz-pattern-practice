import { defineConfig } from '@playwright/test';
import { fileURLToPath } from 'node:url';

export default defineConfig({
  testDir: './tests/browser',
  workers: 1,
  retries: 0,
  reporter: 'list',
  use: {
    baseURL: 'http://127.0.0.1:4287/__path_check__/',
    browserName: 'chromium',
    // Trace snapshots perturb these real-time media checks. Use --trace on
    // explicitly for diagnostics; screenshots and timing JSON remain enabled.
    trace: 'off',
    screenshot: 'only-on-failure',
  },
  webServer: {
    command: 'npm run build -- --outDir .local/path-check && npm run preview -- --outDir .local/path-check --port 4287',
    cwd: fileURLToPath(new URL('.', import.meta.url)),
    url: 'http://127.0.0.1:4287/__path_check__/',
    env: { APP_BASE: '/__path_check__/' },
    reuseExistingServer: false,
    timeout: 60_000,
  },
});
