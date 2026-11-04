import { test, expect } from "./fixtures";
import { expectNoHorizontalOverflow } from "./overflow";

test("the app is drawn right to left in Persian and left to right in English", async ({ page, appLang }) => {
  await page.goto("/");
  await expect(page.locator("html")).toHaveAttribute("lang", appLang);
  await expect(page.locator("html")).toHaveAttribute("dir", appLang === "fa" ? "rtl" : "ltr");
  expect(page.viewportSize()).toEqual({ width: 360, height: 740 });
});

test("a player signs in with the emailed code and lands on their home page", async ({ page, quesshi }) => {
  await quesshi.signIn(page, "player@e2e.test");
  await expect(page).toHaveURL(/\/play$/);
  await expectNoHorizontalOverflow(page);
});

test("a signed-out visitor sees the home page without sideways scrolling", async ({ page }) => {
  await page.goto("/");
  await expectNoHorizontalOverflow(page);
});

for (const [name, path] of [["play", "/play"], ["settings", "/settings"], ["my question sets", "/sets"]] as const) {
  test(`${name} shows no sideways scrolling`, async ({ page, quesshi }) => {
    await quesshi.signIn(page, "player@e2e.test");
    await page.goto(path);
    await expectNoHorizontalOverflow(page);
  });
}
