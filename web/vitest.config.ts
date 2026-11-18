import { defineConfig, mergeConfig } from 'vitest/config'
import viteConfig from './vite.config'

// Reuses the app's plugins and `@/` alias. Playwright specs live in tests/e2e and are never matched.
export default mergeConfig(
  viteConfig,
  defineConfig({
    test: {
      environment: 'jsdom',
      // An origin for relative axios URLs ('/users/...') so msw can match them.
      environmentOptions: { jsdom: { url: 'http://localhost/' } },
      setupFiles: ['./src/test/setup.ts'],
      include: ['src/**/*.test.{ts,tsx}'],
      restoreMocks: true,
      // Page tests take up to ~1.3 s locally and several times that on a loaded CI runner.
      testTimeout: 15_000,
    },
  }),
)
