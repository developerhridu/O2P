import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [
    react(),
    tailwindcss(),
  ],
  server: {
    port: 5151,
    proxy: {
      // Same-origin /api in dev — avoids CORS against the API on :5000.
      // Set VITE_PROXY_TARGET to point a second dev server at a different API (for example a
      // throwaway one for testing) so it can never end up talking to the API on :5000 by accident.
      '/api': {
        target: process.env.VITE_PROXY_TARGET ?? 'http://localhost:5000',
        changeOrigin: true,
      },
    },
  },
})
