/**
 * Vite config variant for the E2E test harness.
 *
 * Mirrors vite.config.ts exactly EXCEPT the proxy target points at the
 * compose harness (E2E_API_URL, default https://localhost:5101) instead of
 * the interactive dev API on :5001. Also adds /test to the proxy map so the
 * global-setup can call POST /test/reset directly from the browser context if
 * needed (though global-setup uses Node https directly, keeping this here for
 * completeness and future use).
 *
 * Usage:
 *   npm run dev:e2e        — starts Vite on :5173 with compose-harness proxying
 *   npm run test:e2e       — Playwright test (spawns dev:e2e automatically)
 *
 * DO NOT modify vite.config.ts — that file continues to target :5001 for
 * interactive development.
 */

import { defineConfig, type ProxyOptions } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import path from 'path'

const E2E_API_URL = process.env['E2E_API_URL'] ?? 'https://localhost:5101'

/** Proxy API requests to the compose harness. Same structure as vite.config.ts. */
function e2eProxy(): ProxyOptions {
  return {
    target: E2E_API_URL,
    changeOrigin: true,
    // self-signed dev cert on the compose harness — skip TLS verification
    secure: false,
    bypass(req) {
      if ((req.headers.accept as string | undefined)?.includes('text/html')) {
        return '/index.html'
      }
    },
  }
}

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  server: {
    port: 5173,
    // The qa-playwright container reaches this dev server by its compose
    // service DNS name (http://web:5173), so Vite 7's host check must allow
    // "web" — otherwise it returns a "Blocked request" page with no SPA and
    // auth.setup can't find the login form. Safe here: this config is only
    // ever served on the isolated qa-net test network.
    allowedHosts: ['web', 'localhost'],
    proxy: {
      '/auth': e2eProxy(),
      '/users': e2eProxy(),
      '/trainer': e2eProxy(),
      '/professionals': e2eProxy(),
      '/swagger': e2eProxy(),
      '/foods': e2eProxy(),
      '/nutrition': e2eProxy(),
      '/recipes': e2eProxy(),
      '/client': e2eProxy(),
      '/payments': e2eProxy(),
      '/admin': e2eProxy(),
      '/exercises': e2eProxy(),
      '/training': e2eProxy(),
      '/conversations': e2eProxy(),
      // Reset endpoint for the e2e harness (POST /test/reset)
      '/test': e2eProxy(),
      '/hubs': {
        target: E2E_API_URL,
        changeOrigin: true,
        secure: false,
        ws: true,
      },
    },
  },
})
