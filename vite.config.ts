import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig(({ mode }) => ({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: { '/api': { target: `http://127.0.0.1:${loadEnv(mode, process.cwd(), 'PORT').PORT || '3001'}`, changeOrigin: false } },
  },
}));
