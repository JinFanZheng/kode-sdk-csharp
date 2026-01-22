import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import path from 'path'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  server: {
    port: 3000,
    proxy: {
      '/v1': {
        target: 'http://localhost:5124',
        changeOrigin: true,
        // Enable streaming support
        ws: true,
        configure: (proxy, _options) => {
          proxy.on('proxyReq', (proxyReq, req, _res) => {
            // Don't buffer for SSE
            if (req.url?.includes('/chat/completions')) {
              proxyReq.setHeader('Connection', 'keep-alive');
            }
          });
          proxy.on('proxyRes', (proxyRes, req, _res) => {
            // Ensure streaming for SSE
            if (req.url?.includes('/chat/completions') && proxyRes.headers['content-type']?.includes('text/event-stream')) {
              proxyRes.headers['connection'] = 'keep-alive';
              delete proxyRes.headers['content-length'];
            }
          });
        },
      },
    },
  },
})
