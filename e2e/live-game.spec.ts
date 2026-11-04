import type { Page } from "@playwright/test";
import { test, expect } from "./fixtures";

/** The host's lobby has one big button: Start. */
const startButton = (page: Page) => page.locator("button.btn--block:not(.lobby-quiet)");

test("a host starts a live game, two players answer a question, and all three see the results", async ({ page: host, quesshi }) => {
  await quesshi.signIn(host, "host@e2e.test");
  await host.locator(".home-primary").click();
  await expect(host).toHaveURL(/\/lobby\/[A-Z0-9]+$/);

  // A new lobby seats two; the host makes room for both players before sharing the code.
  const code = (await host.locator(".lobby-code").innerText()).trim();
  const saved = host.waitForResponse((response) => response.request().method() === "PUT" && response.url().includes("/settings"));
  await host.locator("input[type=number]").fill("3");
  await host.locator("input[type=number] + button").click();
  expect((await saved).ok()).toBe(true);

  // One at a time: the app's mail log is read in order, so two sign-ins at once could take each other's code.
  const players: Page[] = [];
  for (const email of ["one@e2e.test", "two@e2e.test"]) {
    const page = await (await quesshi.newPlayerContext()).newPage();
    await quesshi.signIn(page, email);
    await page.goto(`/join/${code}`);
    await expect(page).toHaveURL(new RegExp(`/lobby/${code}$`));
    players.push(page);
  }

  // The host's lobby fills up over the socket, which is what lets Start through.
  await expect(host.locator(".lobby-tile:not(.lobby-tile--open)")).toHaveCount(3);
  await expect(startButton(host)).toBeEnabled();
  await startButton(host).click();

  // Everyone is taken to the game; each answers the first question once it is open.
  const everyone = [host, ...players];
  for (const page of everyone) {
    await expect(page).toHaveURL(/\/live\//);
    await page.locator(".game-answer").first().click();
  }

  // The reveal shows the right answer and the round's points to each of them.
  for (const page of everyone) {
    await expect(page.locator(".game-stage--reveal")).toBeVisible();
    await expect(page.locator(".answer--right")).toBeVisible();
    await expect(page.locator(".card--flat .numeral").first()).toContainText("+");
  }
});
