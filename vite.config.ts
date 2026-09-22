import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

export default defineConfig({
  base: process.env.APP_BASE ?? '/',
  plugins: [react()],
  server: { host: '127.0.0.1', port: 5187, strictPort: true },
  preview: { host: '127.0.0.1', port: 4187, strictPort: true },
  test: {
    environment: 'jsdom',
    include: ['src/**/*.test.{ts,tsx}'],
    setupFiles: ['./tests/setup.ts'],
  },
});
