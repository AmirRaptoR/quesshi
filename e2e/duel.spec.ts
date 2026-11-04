import type { BrowserContext, Page } from "@playwright/test";
import { test, expect } from "./fixtures";

const ON_RESULT = /\/duel\/[^/]+$/;

/** The question cards animate in and Playwright waits for a still element before each click; this spares every tap that wait. */
async function holdStill(context: BrowserContext): Promise<void> {
  await context.addInitScript(() => {
    document.addEventListener("DOMContentLoaded", () => {
      const style = document.createElement("style");
      style.textContent = "*,*::before,*::after{animation:none!important;transition:none!important}";
      document.head.append(style);
    });
  });
}

/** Opens the Offline tab on the play hub and the "new duel" choices under it. */
async function openDuelChoices(page: Page): Promise<void> {
  await page.goto("/play");
  await page.locator("#home-tab-offline").click();
  await page.locator(".home-primary").click();
}

/** Answers each question with its first option and moves on, until the run is over and the result page opens. */
async function playToTheEnd(page: Page): Promise<void> {
  await expect(page).toHaveURL(/\/play\/[^/]+$/);
  const prompt = page.locator(".game-stage h1");
  for (;;) {
    await expect(prompt).toBeVisible();
    const asked = await prompt.innerText();
    await page.locator(".game-answer").first().click();
    await page.locator(".game-feedback .btn--block").click();
    // A duel's questions are all different, so a new prompt (or the result page) says the click took effect.
    await expect(async () => {
      if (ON_RESULT.test(page.url())) return;
      await expect(prompt).not.toHaveText(asked, { timeout: 500 });
    }).toPass();
    if (ON_RESULT.test(page.url())) return;
  }
}

test("two players play a duel opened with a code from start to the final result", async ({ page, context, quesshi }) => {
  await holdStill(context);
  await quesshi.signIn(page, "host@e2e.test");
  await openDuelChoices(page);
  await page.locator(".home-choice__option").first().click();
  await expect(page).toHaveURL(/\/lobby\/[A-Z0-9]+$/);
  const code = (await page.locator(".lobby-code").innerText()).trim();

  const friendContext = await quesshi.newPlayerContext();
  await holdStill(friendContext);
  const friend = await friendContext.newPage();
  await quesshi.signIn(friend, "friend@e2e.test");
  await openDuelChoices(friend);
  await friend.locator(".home-choice__option").nth(1).click();
  await friend.locator(".home-code input").fill(code);
  await friend.locator(".home-code .btn").click();
  await expect(friend).toHaveURL(new RegExp(`/lobby/${code}$`));
  await expect(friend.locator(".lobby-tile:not(.lobby-tile--open)")).toHaveCount(2);

  // The host's lobby fills as the friend sits down, which is what lets them start.
  const start = page.locator(".btn--block").first();
  await expect(start).toBeEnabled();
  await start.click();
  await expect(page).toHaveURL(/\/play\/[^/]+$/);
  await expect(friend).toHaveURL(/\/play\/[^/]+$/);

  await playToTheEnd(page);
  await playToTheEnd(friend);

  // The second player to finish settles the duel; the first one's result page was drawn before that, so it is opened again.
  await expect(friend.locator(".stand-row")).toHaveCount(2);
  await page.reload();
  await expect(page.locator(".stand-row")).toHaveCount(2);
  for (const side of [page, friend]) {
    await expect(side.locator(".stand-row--me .chip")).toBeVisible();
    await expect(side.locator(".game-results h2")).toBeVisible();
  }
});
