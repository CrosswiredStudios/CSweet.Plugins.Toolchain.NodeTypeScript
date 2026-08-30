import { expect, test } from "@playwright/test";
test("renders a canvas", async ({ page }) => { await page.goto("/"); await expect(page.locator("canvas")).toBeVisible(); await page.screenshot({ path: "test-results/babylon.png" }); });
