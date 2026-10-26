import { defineConfig } from "@playwright/test";
export default defineConfig({
  workers: 1,
  webServer: { command: "npm run build && npx vite preview --host 127.0.0.1", port: 4173 },
  use: { baseURL: "http://127.0.0.1:4173", viewport: { width: 640, height: 360 }, deviceScaleFactor: 1,
    locale: "en-US", timezoneId: "UTC", colorScheme: "dark", reducedMotion: "reduce", video: "off", trace: "off",
    launchOptions: { args: ["--use-gl=angle", "--use-angle=swiftshader", "--enable-unsafe-swiftshader"] } }
});
