import { expect, test } from "@playwright/test";

test("renders the Phaser canvas", async ({ page }) => {
  await page.goto("/");
  await expect(page.locator("canvas")).toBeVisible();
  await page.screenshot({ path: "test-results/phaser.png" });
});
