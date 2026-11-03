import { afterEach, describe, expect, test } from "bun:test";
import { testApp } from "../../tests/support/app";
import { defineFeature } from "./feature";
import { refuse } from "./kind";

let app: ReturnType<typeof testApp>;
afterEach(() => app.cleanup());

/** A feature with one levelled kind and one without levels, standing in for trivia and polls. */
const quiz = defineFeature({
  name: "quiz",
  kinds: {
    yesno: { levelled: true, check: (p) => (typeof (p as { yes?: unknown })?.yes === "boolean" ? { ok: true, payload: { yes: (p as { yes: boolean }).yes } } : refuse("bad_answer")) },
    opinion: { levelled: false, check: () => ({ ok: true, payload: {} }) },
  },
});

async function asAdmin() {
  app = testApp([quiz]);
  app.questions.addCategory({ id: "geo", nameFa: "جغرافیا", nameEn: "Geography", nameNl: "Aardrijkskunde", icon: "🌍", color: "#2a9d8f" });
  return app.signInAdmin();
}
const question = (fields: Record<string, unknown> = {}) => ({ kind: "yesno", lang: "en", categoryId: "geo", level: 2, prompt: " Is Amsterdam the capital? ", payload: { yes: true, extra: 1 }, explanation: "It is.", ...fields });
const save = (cookie: string, body: unknown) => app.call("POST", "/api/admin/questions", body, cookie);

describe("admin questions", () => {
  test("a new question is checked by its kind, trimmed, and pending unless a status is given", async () => {
    const admin = await asAdmin();

    const saved = (await save(admin, question())).body;

    expect(saved).toMatchObject({ kind: "yesno", lang: "en", categoryId: "geo", level: 2, prompt: "Is Amsterdam the capital?", payload: { yes: true }, explanation: "It is.", status: "pending" });
    expect((await save(admin, question({ status: "approved" }))).body.status).toBe("approved");
  });

  test("an edit keeps the id and replaces the fields", async () => {
    const admin = await asAdmin();
    const { id } = (await save(admin, question())).body;

    await save(admin, question({ id, prompt: "Is Utrecht the capital?", payload: { yes: false } }));

    expect((await app.call("GET", "/api/admin/questions", undefined, admin)).body).toMatchObject({ total: 1, items: [{ id, prompt: "Is Utrecht the capital?", payload: { yes: false } }] });
  });

  test("refuses each broken envelope rule, and passes on the kind's own code", async () => {
    const admin = await asAdmin();

    expect((await save(admin, question({ kind: "essay" }))).body).toEqual({ error: "bad_kind" });
    expect((await save(admin, question({ lang: "de" }))).body).toEqual({ error: "bad_lang" });
    expect((await save(admin, question({ level: 6 }))).body).toEqual({ error: "bad_level" });
    expect((await save(admin, question({ kind: "opinion", level: 2 }))).body).toEqual({ error: "bad_level" });
    expect((await save(admin, question({ prompt: " " }))).body).toEqual({ error: "bad_prompt" });
    expect((await save(admin, question({ categoryId: "nope" }))).body).toEqual({ error: "bad_category" });
    expect((await save(admin, question({ status: "maybe" }))).body).toEqual({ error: "bad_status" });
    expect((await save(admin, question({ payload: {} }))).body).toEqual({ error: "bad_answer" });
    expect((await save(admin, question({ id: "missing" }))).status).toBe(404);
  });

  test("the list filters by language, category, level, status, kind and text, newest first, a page at a time", async () => {
    const admin = await asAdmin();
    for (let i = 0; i < 3; i++) {
      app.scheduler.advanceTo(Date.parse("2026-11-03T12:00:00Z") + i * 1000);
      await save(admin, question({ prompt: `Capital ${i}?`, level: i + 1 }));
    }
    await save(admin, question({ kind: "opinion", level: null, prompt: "Tea or coffee?", lang: "fa", status: "approved" }));
    const list = async (query: string) => (await app.call("GET", `/api/admin/questions?${query}`, undefined, admin)).body;
    const prompts = async (query: string) => (await list(query)).items.map((q: { prompt: string }) => q.prompt);

    expect(await prompts("lang=fa")).toEqual(["Tea or coffee?"]);
    expect(await prompts("kind=yesno&level=2")).toEqual(["Capital 1?"]);
    expect(await prompts("status=pending&category=geo")).toEqual(["Capital 2?", "Capital 1?", "Capital 0?"]);
    expect(await prompts("text=capital 0")).toEqual(["Capital 0?"]);
    expect(await list("kind=yesno&skip=1&take=1")).toMatchObject({ total: 3, items: [{ prompt: "Capital 1?" }] });
  });

  test("approve, reject and delete", async () => {
    const admin = await asAdmin();
    const { id } = (await save(admin, question())).body;
    const status = async () => (await app.call("GET", "/api/admin/questions", undefined, admin)).body.items[0]?.status;

    await app.call("POST", `/api/admin/questions/${id}/approve`, undefined, admin);
    expect(await status()).toBe("approved");
    await app.call("POST", `/api/admin/questions/${id}/reject`, undefined, admin);
    expect(await status()).toBe("rejected");
    await app.call("DELETE", `/api/admin/questions/${id}`, undefined, admin);
    expect(await status()).toBeUndefined();
  });

  test("the kinds each feature offers are listed for the form", async () => {
    const admin = await asAdmin();

    expect((await app.call("GET", "/api/admin/question-kinds", undefined, admin)).body).toEqual([
      { kind: "yesno", feature: "quiz", levelled: true }, { kind: "opinion", feature: "quiz", levelled: false },
    ]);
  });
});
