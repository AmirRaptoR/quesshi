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
    yesno: {
      levelled: true,
      check: (p) => (typeof (p as { yes?: unknown })?.yes === "boolean" ? { ok: true, payload: { yes: (p as { yes: boolean }).yes } } : refuse("bad_answer")),
      columns: ["yes"],
      fromRow: (row) => ({ yes: row.yes === undefined ? undefined : row.yes === "true" }),
      example: { prompt: "Is water wet?", yes: "true" },
    },
    opinion: { levelled: false, check: () => ({ ok: true, payload: {} }), columns: [], fromRow: () => ({}), example: { prompt: "Tea?" } },
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

describe("importing questions", () => {
  const csv = [
    "lang,categoryId,level,prompt,yes,explanation,status",
    'en,geo,2,"Is Amsterdam the capital, still?",true,,approved',
    "en,geo,9,Too hard?,true,,",
    "en,geo,1,No answer?,,,",
    "de,geo,1,Ist das so?,true,,",
  ].join("\r\n");
  const importing = (cookie: string, query: string, content: string, type = "text/csv") => app.upload(`/api/admin/questions/import?${query}`, content, type, cookie);

  test("a dry run reports every row and writes nothing", async () => {
    const admin = await asAdmin();

    const report = (await importing(admin, "kind=yesno&format=csv", csv)).body;

    expect(report).toEqual({
      dryRun: true, total: 4, accepted: 1, rejected: 3,
      rows: [
        { row: 1, prompt: "Is Amsterdam the capital, still?", accepted: true, error: null },
        { row: 2, prompt: "Too hard?", accepted: false, error: "bad_level" },
        { row: 3, prompt: "No answer?", accepted: false, error: "bad_answer" },
        { row: 4, prompt: "Ist das so?", accepted: false, error: "bad_lang" },
      ],
    });
    expect(app.questions.find({ skip: 0, take: 10 }).total).toBe(0);
  });

  test("a real run writes the accepted rows", async () => {
    const admin = await asAdmin();

    await importing(admin, "kind=yesno&format=csv&dryRun=false", csv);

    expect(app.questions.find({ skip: 0, take: 10 }).items).toMatchObject([{ prompt: "Is Amsterdam the capital, still?", payload: { yes: true }, status: "approved", level: 2 }]);
  });

  test("JSON files hold the same rows, and a question without levels needs no level", async () => {
    const admin = await asAdmin();
    const rows = [{ lang: "en", categoryId: "geo", prompt: "Tea or coffee?" }];

    expect((await importing(admin, "kind=opinion&format=json", JSON.stringify(rows), "application/json")).body).toMatchObject({ accepted: 1 });
  });

  test("refuses a file it cannot read as a whole", async () => {
    const admin = await asAdmin();
    const error = async (query: string, content: string) => (await importing(admin, query, content)).body;

    expect(await error("kind=essay&format=csv", csv)).toEqual({ error: "bad_kind" });
    expect(await error("kind=yesno&format=xlsx", csv)).toEqual({ error: "bad_format" });
    expect(await error("kind=yesno&format=csv", "")).toEqual({ error: "empty_file" });
    expect(await error("kind=yesno&format=csv", "lang,prompt\nen,Hi?")).toEqual({ error: "bad_row" });
    expect(await error("kind=yesno&format=json", "{")).toEqual({ error: "bad_json" });
    expect(await error("kind=yesno&format=csv", [csv, ...Array(2000).fill("en,geo,1,More?,true,,")].join("\n"))).toEqual({ error: "too_many_rows" });
  });

  test("the template holds the shared columns and the kind's example", async () => {
    const admin = await asAdmin();

    const response = await app.call("GET", "/api/admin/questions/import/template?kind=yesno&format=csv", undefined, admin);

    expect(response.type).toStartWith("text/csv");
    expect(response.text).toBe("lang,categoryId,level,prompt,yes,explanation,status\r\nen,geography,2,Is water wet?,true,,pending\r\n");
  });
});
