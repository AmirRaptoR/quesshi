import { expect, type Page } from "@playwright/test";

/** Waits until the page has drawn (no spinner left, its shell showing), then checks nothing scrolls sideways. */
export async function expectNoHorizontalOverflow(page: Page): Promise<void> {
  await expect(page.locator(".spinner")).toHaveCount(0);
  await expect(page.locator(".shell").first()).toBeVisible();
  const { scrollWidth, clientWidth } = await page.evaluate(() => ({
    scrollWidth: document.documentElement.scrollWidth, clientWidth: document.documentElement.clientWidth,
  }));
  expect(scrollWidth).toBeLessThanOrEqual(clientWidth);
}
