import { ADMIN } from "./admin";
import { test, expect } from "./fixtures";
import { MIN_APPROVED_QUESTIONS_PER_LANGUAGE } from "../server/core/questions.store";

test("a fresh app holds the seeded categories, approved questions and the one administrator", async ({ context, app, quesshi }) => {
  await quesshi.signInAdmin(context);
  const headers = { origin: app.origin };

  const me = await context.request.get(`${app.origin}/api/admin/auth/me`, { headers });
  expect(me.ok()).toBe(true);
  expect(((await me.json()) as { username: string }).username).toBe(ADMIN.username);

  const categories = (await (await context.request.get(`${app.origin}/api/admin/categories`, { headers })).json()) as { id: string }[];
  expect(categories.map((category) => category.id).sort()).toEqual(["art", "geo", "sci"]);

  const dashboard = (await (await context.request.get(`${app.origin}/api/admin/dashboard`, { headers })).json()) as {
    questions: { approved: number; pending: number };
  };
  expect(dashboard.questions.approved).toBe(categories.length * 2 * MIN_APPROVED_QUESTIONS_PER_LANGUAGE);
  expect(dashboard.questions.pending).toBe(0);
});

test("a player who signs in is counted in this test's database", async ({ page, context, app, quesshi }) => {
  await quesshi.signIn(page, "first@e2e.test");
  await quesshi.signInAdmin(context);
  const response = await context.request.get(`${app.origin}/api/admin/dashboard`, { headers: { origin: app.origin } });
  expect(((await response.json()) as { players: number }).players).toBe(1);
});

test("another test's database starts without that player", async ({ context, app, quesshi }) => {
  await quesshi.signInAdmin(context);
  const response = await context.request.get(`${app.origin}/api/admin/dashboard`, { headers: { origin: app.origin } });
  expect(((await response.json()) as { players: number }).players).toBe(0);
});
