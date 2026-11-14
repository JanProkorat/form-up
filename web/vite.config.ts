import { defineConfig, type ProxyOptions } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import path from 'path'
import { appLinks } from './vite-plugins/appLinks'

/** Proxy API requests to the backend but let browser navigations fall through to index.html. */
function apiProxy(): ProxyOptions {
  return {
    target: 'https://localhost:5001',
    changeOrigin: true,
    secure: false,
    bypass(req) {
      if ((req.headers.accept as string | undefined)?.includes('text/html')) {
        return '/index.html'
      }
    },
  }
}

export default defineConfig({
  plugins: [react(), tailwindcss(), appLinks()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  server: {
    port: 5173,
    proxy: {
      '/auth': apiProxy(),
      '/users': apiProxy(),
      '/trainer': apiProxy(),
      '/professionals': apiProxy(),
      '/swagger': apiProxy(),
      '/foods': apiProxy(),
      '/nutrition': apiProxy(),
      '/recipes': apiProxy(),
      '/client': apiProxy(),
      '/payments': apiProxy(),
      '/billing': apiProxy(),
      '/admin': apiProxy(),
      '/exercises': apiProxy(),
      '/training': apiProxy(),
      '/conversations': apiProxy(),
      '/hubs': {
        target: 'https://localhost:5001',
        changeOrigin: true,
        secure: false,
        ws: true,
      },
    },
  },
})
