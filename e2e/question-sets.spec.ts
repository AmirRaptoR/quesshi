import { test, expect } from "./fixtures";
import { expectNoHorizontalOverflow } from "./overflow";

const SET_NAME = "E2E set";

test("a player makes a question set from the template's example answer and hosts a game from it", async ({ page, quesshi }) => {
  await quesshi.signIn(page, "host@e2e.test");

  // My question sets offers a new one.
  await page.goto("/sets");
  await expectNoHorizontalOverflow(page);
  await page.locator("a.btn[href='/sets/new']").click();
  await expect(page).toHaveURL(/\/sets\/new$/);
  await page.locator("#set-name").fill(SET_NAME);

  // The prompt ends with the example answer; pasting it back is what an AI's reply would be.
  const prompt = await page.locator("textarea[readonly]").inputValue();
  const example = prompt.slice(prompt.lastIndexOf("\n[") + 1);
  expect(JSON.parse(example)).toHaveLength(1);
  await page.locator("#set-answer").fill(example);

  const imported = page.waitForResponse((response) => response.request().method() === "POST" && response.url().endsWith("/import"));
  await page.locator("#set-answer").locator("xpath=following::button[1]").click();
  expect((await imported).ok()).toBe(true);
  await expect(page.locator("p.notice--good")).toBeVisible();
  await expectNoHorizontalOverflow(page);

  // The set now holds the imported question.
  await page.locator("a.btn--ghost[href^='/sets/']").click();
  await expect(page).toHaveURL(/\/sets\/[^/]+$/);
  const setId = new URL(page.url()).pathname.split("/").pop()!;
  await expect(page.locator(".topbar__title")).toHaveText(SET_NAME);
  await expect(page.getByText("Mars").first()).toBeVisible();
  await expectNoHorizontalOverflow(page);

  // Host a game from the set: pick it on the questions sheet, then open the lobby.
  await page.goto("/play");
  await page.locator(".home-settings").click();
  await page.locator(".picker__chip", { hasText: SET_NAME }).click();
  await page.locator(".btn--done").click();
  await expect(page).toHaveURL(/\/play$/);
  await page.locator(".home-primary").click();
  await expect(page).toHaveURL(/\/lobby\/[A-Z0-9]+$/);
  await expect(page.locator(".lobby-code")).toBeVisible();
  // The room was made with the chosen set, not an ordinary game.
  await expect(page.locator(".lobby-settings-line")).toHaveAttribute("href", new RegExp(`[?&]qs=${setId}(&|$)`));
});
