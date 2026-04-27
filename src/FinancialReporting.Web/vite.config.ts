import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

const apiTarget = process.env.VITE_API_TARGET ?? 'http://localhost:5264'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': apiTarget,
      '/hubs': {
        target: apiTarget,
        ws: true,
      },
    },
  },
})
