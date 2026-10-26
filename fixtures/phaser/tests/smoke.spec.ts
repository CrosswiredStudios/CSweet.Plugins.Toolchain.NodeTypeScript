import { expect, test } from "@playwright/test";

test("renders the Phaser canvas", async ({ page }) => {
  await page.goto("/");
  await expect(page.locator("canvas")).toBeVisible();
  await expect(page.locator("body")).toHaveAttribute("data-fixture-ready", "true");
  await page.screenshot({ path: "test-results/phaser.png" });
});
