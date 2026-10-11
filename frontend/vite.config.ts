import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
export default defineConfig({
  plugins: [react()],
  server: {
    port: 4200,
    proxy: {
      '/operations.html': { target: 'http://localhost:5241', rewrite: () => '/' },
      '/app.js': 'http://localhost:5241',
      '/styles.css': 'http://localhost:5241',
      '/api': 'http://localhost:5241',
      '/uploads': 'http://localhost:5241',
      '/demo': 'http://localhost:5241',
    },
  },
})
