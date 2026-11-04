import { test, expect } from "./fixtures";

const PROMPT = "Which planet is known as the red planet?";

test("an admin approves a pending question and sees it approved in the list", async ({ page, context, app, quesshi }) => {
  await quesshi.signInAdmin(context);
  // A question saved as pending waits for review, as one an admin drafts or imports does.
  const created = await context.request.post(`${app.origin}/api/admin/questions`, {
    headers: { origin: app.origin },
    data: {
      kind: "choice", lang: "en", categoryId: "sci", level: 2, status: "pending", prompt: PROMPT,
      payload: { options: ["Mars", "Venus", "Jupiter", "Saturn"], correctIndex: 0 }, explanation: null, media: null,
    },
  });
  expect(created.ok()).toBe(true);

  await page.goto("/admin/questions");
  // Narrow the list to the one question; the seeded bank has many approved ones.
  await page.locator("input.input.grow").fill(PROMPT);
  await page.locator("input.input.grow").press("Enter");
  const card = page.locator(".admin-question").filter({ hasText: PROMPT });
  await expect(card).toHaveCount(1);
  await expect(card.locator(".admin-status")).not.toHaveClass(/is-active/);

  const approved = page.waitForResponse((response) => response.request().method() === "POST" && response.url().endsWith("/approve"));
  await card.locator(".admin-question__actions .btn:not(.btn--ghost)").click();
  expect((await approved).ok()).toBe(true);

  // The list reloads after the action and shows the question as approved, with nothing left to approve.
  await expect(card.locator(".admin-status")).toHaveClass(/is-active/);
  await expect(card.locator(".admin-question__actions .btn:not(.btn--ghost)")).toHaveCount(0);
});
