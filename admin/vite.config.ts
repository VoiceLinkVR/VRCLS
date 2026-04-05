import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  base: '/admin/',
  plugins: [vue()],
  server: {
    proxy: {
      '/adminapi': 'http://127.0.0.1:8980',
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
