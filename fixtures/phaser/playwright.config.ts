import { defineConfig } from "@playwright/test";
export default defineConfig({ webServer: { command: "npm run build && npx vite preview --host 127.0.0.1", port: 4173 }, use: { baseURL: "http://127.0.0.1:4173", video: "on" } });
