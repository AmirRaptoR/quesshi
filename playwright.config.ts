import { defineConfig } from "@playwright/test";
import type { Lang } from "./e2e/fixtures";

/** The phone every test runs on, once per language: Persian (right to left, the default) and English. */
const project = (appLang: Lang) => ({
  name: appLang,
  use: { browserName: "chromium", viewport: { width: 360, height: 740 }, appLang, locale: appLang === "fa" ? "fa-IR" : "en-US" },
}) as const;

export default defineConfig({
  testDir: "e2e",
  testMatch: "*.spec.ts",
  fullyParallel: true,
  forbidOnly: Boolean(process.env.CI),
  reporter: process.env.CI ? [["list"], ["html", { open: "never" }]] : "list",
  outputDir: "test-results",
  use: { trace: "retain-on-failure", screenshot: "only-on-failure" },
  projects: [project("fa"), project("en")],
});
