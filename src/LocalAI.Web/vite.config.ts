import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";

// In development the dashboard calls /api on its own origin and Vite proxies it to
// the ASP.NET Core API, so no CORS configuration is needed.
const devApiTarget = process.env.VITE_DEV_API_PROXY ?? "http://localhost:5080";

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      "/api": { target: devApiTarget, changeOrigin: false },
    },
  },
  build: {
    sourcemap: false,
    target: "es2022",
  },
  test: {
    environment: "jsdom",
    setupFiles: ["./src/test/setup.ts"],
    restoreMocks: true,
  },
});
