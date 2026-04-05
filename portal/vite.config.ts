import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// https://vite.dev/config/
export default defineConfig({
  base: '/app/',
  plugins: [vue()],
  server: {
    proxy: {
      '/auth': 'http://127.0.0.1:8980',
      '/api': 'http://127.0.0.1:8980',
      '/manageapi': 'http://127.0.0.1:8980',
      '/ui': 'http://127.0.0.1:8980',
    },
  },
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
})
