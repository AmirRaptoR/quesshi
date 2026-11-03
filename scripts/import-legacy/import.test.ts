import { afterEach, describe, expect, test } from "bun:test";
import { mkdirSync, readdirSync, writeFileSync } from "node:fs";
import path from "node:path";
import { createQuestionsStore } from "../../server/core/questions.store";
import { tempWorld } from "../../tests/support/temp";
import { importLegacy } from "./import";

let world: ReturnType<typeof tempWorld>;
afterEach(() => world.cleanup());

const date = { $date: "2026-09-01T10:00:00Z" };
const base = { Family: 0, OwnerId: null, Lang: 1, CategoryId: "geo", Level: 2, Choices: [], CorrectIndex: 0, MediaKind: 0, MediaUrl: "", Status: 1, CreatedAt: date, Explanation: null };
const docs = {
  categories: [{ _id: "geo", NameFa: "جغرافیا", NameEn: "Geography", NameNl: "Aardrijkskunde", Icon: "🌍", Color: "#2a9d8f", IsActive: true, SortOrder: 1 }],
  content_settings: [{ _id: "settings", TriviaCategoryIds: ["geo"], VotingCategoryIds: ["geo", "gone"] }],
  questions: [
    { ...base, _id: "c1", Prompt: "Capital of the Netherlands?", Choices: ["Amsterdam", "Rotterdam", "Utrecht", "The Hague"], CorrectIndex: 0, Explanation: "It is." },
    { ...base, _id: "c2", Kind: 0, Lang: 0, Status: 0, Prompt: "پایتخت؟", Choices: ["الف", "ب", "پ", "ت"], CorrectIndex: 3, MediaKind: 1, MediaUrl: "/media/uploads/flag.png", MediaAttribution: "Photo: Ann" },
    { ...base, _id: "s1", Kind: 1, Prompt: "Largest first", Choices: ["Asia", "Africa", "Europe", "Oceania"] },
    { ...base, _id: "m1", Kind: 2, Lang: 2, Prompt: "Waar ligt Nederland?", TargetShape: 0, TargetCountryCode: "NL", BaseLayer: 1 },
    { ...base, _id: "m2", Kind: 2, Prompt: "Find Amsterdam", TargetShape: 1, TargetLatitude: 52.37, TargetLongitude: 4.9, TargetRadiusKm: 50, BaseLayer: 0, MediaKind: 2, MediaUrl: "https://media.example.com/a.mp3" },
    { ...base, _id: "p1", Kind: 3, Level: 1, Prompt: "Who sleeps longest?" },
    { ...base, _id: "v1", Family: 1, Level: null, CorrectIndex: null, Prompt: "Tea or coffee?", AnswerSource: 1, FixedChoices: ["Tea", "Coffee"], Status: 2 },
    { ...base, _id: "v2", Family: 1, Level: null, CorrectIndex: null, Prompt: "Who is the funniest?", AnswerSource: 0, FixedChoices: [] },
    { ...base, _id: "mine", OwnerId: "player-7", Prompt: "A player's own question?", Choices: ["A", "B", "C", "D"] },
    { ...base, _id: "bad", Prompt: "Three choices?", Choices: ["A", "B", "C"] },
  ],
};

function exported(extra: Partial<typeof docs> = {}) {
  world = tempWorld();
  const folder = path.join(world.mediaDir, "..", "export");
  mkdirSync(path.join(folder, "media", "uploads"), { recursive: true });
  for (const [name, rows] of Object.entries({ ...docs, ...extra })) writeFileSync(path.join(folder, `${name}.json`), JSON.stringify(rows));
  writeFileSync(path.join(folder, "media", "uploads", "flag.png"), "png");
  return folder;
}
const run = (folder: string) => importLegacy({ exportDir: folder, db: world.db, mediaDir: world.mediaDir, clock: { now: () => new Date("2026-11-03T12:00:00Z") } });

describe("importing today's question bank", () => {
  test("turns each of today's kinds into the new one, with its language, level, status and explanation", () => {
    run(exported());
    const q = (id: string) => createQuestionsStore(world.db).question(id);

    expect(q("c1")).toMatchObject({ kind: "choice", lang: "en", level: 2, status: "approved", categoryId: "geo", payload: { options: ["Amsterdam", "Rotterdam", "Utrecht", "The Hague"], correctIndex: 0 }, explanation: "It is.", createdAt: "2026-09-01T10:00:00.000Z" });
    expect(q("c2")).toMatchObject({ kind: "choice", lang: "fa", status: "pending", payload: { correctIndex: 3 } });
    expect(q("s1")?.payload).toEqual({ items: [{ id: "1", text: "Asia" }, { id: "2", text: "Africa" }, { id: "3", text: "Europe" }, { id: "4", text: "Oceania" }] });
    expect(q("m1")).toMatchObject({ kind: "map_pick", lang: "nl", payload: { baseLayer: "borders", target: { shape: "country", countryCode: "NL" } } });
    expect(q("m2")?.payload).toEqual({ baseLayer: "blank", target: { shape: "point", latitude: 52.37, longitude: 4.9, radiusKm: 50 } });
    expect(q("p1")).toMatchObject({ kind: "poll_roster", level: null, payload: {} });
    expect(q("v1")).toMatchObject({ kind: "poll_choice", level: null, status: "rejected", payload: { options: ["Tea", "Coffee"] } });
    expect(q("v2")).toMatchObject({ kind: "poll_roster", level: null });
  });

  test("leaves out players' own questions and those today's rules no longer accept, and says why", () => {
    const report = run(exported());

    expect(report.imported).toBe(8);
    expect(report.skipped).toEqual([{ id: "mine", reason: "player_authored" }, { id: "bad", reason: "bad_choices" }]);
  });

  test("copies local media under new names and keeps links to other sites", () => {
    run(exported());
    const q = (id: string) => createQuestionsStore(world.db).question(id);

    const copied = q("c2")!.media!;
    expect(copied).toMatchObject({ kind: "image", attribution: "Photo: Ann" });
    expect(copied.url).toMatch(/^\/media\/uploads\/[0-9a-f-]{36}\.png$/);
    expect(readdirSync(path.join(world.mediaDir, "uploads"))).toEqual([path.basename(copied.url)]);
    expect(q("m2")!.media).toEqual({ kind: "audio", url: "https://media.example.com/a.mp3", attribution: null });
  });

  test("a question whose local media file is missing is left out", () => {
    const report = run(exported({ questions: [{ ...docs.questions[1]!, MediaUrl: "/media/uploads/nope.png" }] }));

    expect(report.skipped).toEqual([{ id: "c2", reason: "missing_media" }]);
  });

  test("brings the categories and which ones trivia and polls offer, dropping unknown ones", () => {
    run(exported());
    const store = createQuestionsStore(world.db);

    expect(store.categories()).toEqual([{ id: "geo", nameFa: "جغرافیا", nameEn: "Geography", nameNl: "Aardrijkskunde", icon: "🌍", color: "#2a9d8f", isActive: true, sortOrder: 1 }]);
    expect(store.offers("trivia")).toEqual(["geo"]);
    expect(store.offers("polls")).toEqual(["geo"]);
  });

  test("running it again adds nothing", () => {
    const folder = exported();
    run(folder);

    const again = run(folder);

    expect(again.imported).toBe(0);
    expect(createQuestionsStore(world.db).find({ skip: 0, take: 100 }).total).toBe(8);
  });
});
