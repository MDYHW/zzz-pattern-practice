import { afterEach, expect, test, vi } from 'vitest';

afterEach(() => {
  document.querySelectorAll('script[data-cf-beacon]').forEach(script => script.remove());
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
});

test.each([
  [true, 'zzz-pattern-practice.github.io', 1],
  [true, '127.0.0.1', 0],
  [true, 'example.com', 0],
  [false, 'zzz-pattern-practice.github.io', 0],
])('beacon production=%s host=%s', async (production, hostname, count) => {
  vi.resetModules();
  vi.stubEnv('PROD', production);
  vi.stubGlobal('window', { location: { hostname } });
  await import('./analytics');
  const scripts = document.querySelectorAll<HTMLScriptElement>('script[data-cf-beacon]');
  expect(scripts).toHaveLength(count);
  if (count) {
    expect(scripts[0].src).toBe('https://static.cloudflareinsights.com/beacon.min.js');
    expect(scripts[0].type).toBe('module');
  }
});
