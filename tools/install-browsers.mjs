import { spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const result = spawnSync(process.execPath, [require.resolve('@playwright/test/cli'), 'install', 'chromium'], {
  stdio: 'inherit',
  env: { ...process.env, PLAYWRIGHT_SKIP_BROWSER_GC: '1' },
});
if (result.error) throw result.error;
process.exit(result.status ?? 1);
