import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'

export default defineConfig({
  plugins: [react()],
  /*
   * Both ports are pinned rather than preferred.
   *
   * The backend does not wildcard its allowed origins -- it cannot, because the telemetry
   * hub's WebSocket handshake is checked against the same list and a browser refuses a
   * credentialed response to `*`. `http://localhost:5173` and `http://localhost:8080` are
   * what `appsettings.Development.json` and `integration/docker-compose.yml` list, so a
   * dev server that quietly walked to 5174 because 5173 was still held by a previous run
   * would come up looking fine and fail every call with a CORS error. Failing to start is
   * the more honest outcome: it names the port that is in the way.
   */
  server: { host: true, port: 5173, strictPort: true },
  preview: { host: true, port: 8080, strictPort: true },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['src/test/setup.ts'],
    css: false,
    coverage: { provider: 'v8', include: ['src/**'], exclude: ['src/test/**', 'src/main.tsx'] },
  },
})
