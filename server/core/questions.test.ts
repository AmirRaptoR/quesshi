import { afterEach, describe, expect, test } from "bun:test";
import { tempWorld } from "../../tests/support/temp";
import { drawQuestions, levelForSlot, NotEnoughQuestions } from "./questions";
import { createQuestionsStore, MIN_APPROVED_QUESTIONS_PER_LANGUAGE } from "./questions.store";

let world: ReturnType<typeof tempWorld> | undefined;
afterEach(() => {
  world?.cleanup();
  world = undefined;
});

function bank() {
  world = tempWorld();
  world.migrateCore();
  const store = createQuestionsStore(world.db);
  let n = 0;
  const add = (q: Partial<{ lang: string; categoryId: string; level: number; status: string; kind: string }> = {}) => {
    const id = `q${++n}`;
    store.add({ id, lang: q.lang ?? "en", categoryId: q.categoryId ?? "geo", level: q.level ?? 3, status: q.status ?? "approved", kind: q.kind ?? "choice", prompt: id, payload: {}, explanation: null, createdAt: "2026-11-03T12:00:00Z" });
    return id;
  };
  for (const id of ["geo", "sport", "art"]) store.addCategory({ id, nameFa: id, nameEn: id, nameNl: id, icon: "x", color: "#000" });
  return { store, add };
}

const draw = (store: ReturnType<typeof createQuestionsStore>, options: Partial<Parameters<typeof drawQuestions>[1]> = {}) =>
  drawQuestions(store, { lang: "en", categoryIds: null, levels: [], count: 2, kinds: ["choice"], ...options });

describe("levelForSlot", () => {
  test("ramps from very easy to very hard over the game", () => {
    expect(Array.from({ length: 10 }, (_, slot) => levelForSlot(slot, 10, [1, 2, 3, 4, 5]))).toEqual([1, 1, 2, 2, 3, 3, 4, 4, 5, 5]);
  });

  test("with chosen levels, the ramp spreads over just those", () => {
    expect(Array.from({ length: 10 }, (_, slot) => levelForSlot(slot, 10, [2, 4]))).toEqual([2, 2, 2, 2, 2, 4, 4, 4, 4, 4]);
  });
});

describe("drawQuestions", () => {
  test("draws distinct approved questions in the game's language and accepted kinds only", () => {
    const { store, add } = bank();
    const wanted = [add(), add(), add()];
    add({ status: "pending" });
    add({ lang: "fa" });
    add({ kind: "poll_choice" });

    const drawn = draw(store, { count: 3 });

    expect(drawn.map((q) => q.id).sort()).toEqual(wanted.sort());
  });

  test("takes chosen categories in turn, slot by slot", () => {
    const { store, add } = bank();
    add({ categoryId: "geo" }); add({ categoryId: "geo" });
    add({ categoryId: "sport" }); add({ categoryId: "sport" });

    const drawn = draw(store, { count: 4, categoryIds: ["geo", "sport"] });

    expect(drawn.map((q) => q.categoryId)).toEqual(["geo", "sport", "geo", "sport"]);
  });

  test("falls back to another chosen category, then to another chosen level", () => {
    const { store, add } = bank();
    const sportEasy = add({ categoryId: "sport", level: 1 });
    const geoMedium = add({ categoryId: "geo", level: 3 });

    // Slot 0 wants geo at level 1 (none: sport has one); slot 1 wants sport at level 5 (none anywhere: level 3 has one).
    const drawn = draw(store, { count: 2, categoryIds: ["geo", "sport"], levels: [1, 3, 5] });

    expect(drawn.map((q) => q.id)).toEqual([sportEasy, geoMedium]);
  });

  test("draws questions without a level when the game has no levels", () => {
    const { store, add } = bank();
    world!.db.run("update questions set level = null");
    add({ kind: "poll_choice" });
    world!.db.run("update questions set level = null");

    const drawn = draw(store, { count: 1, levels: null, kinds: ["poll_choice"] });

    expect(drawn.map((q) => q.kind)).toEqual(["poll_choice"]);
  });

  test("fails when the bank cannot fill the game", () => {
    const { store, add } = bank();
    add();

    expect(() => draw(store, { count: 2 })).toThrow(NotEnoughQuestions);
    expect(() => draw(store, { count: 1, categoryIds: [] })).toThrow(NotEnoughQuestions);
  });
});

describe("offeredCategories", () => {
  const MIN = MIN_APPROVED_QUESTIONS_PER_LANGUAGE;
  const offered = (store: ReturnType<typeof createQuestionsStore>) => {
    store.setOffers("trivia", ["geo", "sport"]);
    return store.offeredCategories("trivia", ["choice"]);
  };
  const addMany = (add: ReturnType<typeof bank>["add"], n: number, q: Parameters<ReturnType<typeof bank>["add"]>[0] = {}) => {
    for (let i = 0; i < n; i++) add(q);
  };

  test("the minimum is 30", () => {
    expect(MIN).toBe(30);
  });

  test("a language with exactly the minimum of approved questions is listed", () => {
    const { store, add } = bank();
    addMany(add, MIN, { lang: "fa" });
    expect(offered(store).find((c) => c.id === "geo")!.langs).toEqual(["fa"]);
  });

  test("a language one short of the minimum is not listed", () => {
    const { store, add } = bank();
    addMany(add, MIN - 1, { lang: "fa" });
    expect(offered(store).find((c) => c.id === "geo")!.langs).toEqual([]);
  });

  test("pending questions do not count", () => {
    const { store, add } = bank();
    addMany(add, MIN - 1, { lang: "fa" });
    addMany(add, 5, { lang: "fa", status: "pending" });
    expect(offered(store).find((c) => c.id === "geo")!.langs).toEqual([]);
  });

  test("rejected questions do not count", () => {
    const { store, add } = bank();
    addMany(add, MIN - 1, { lang: "fa" });
    addMany(add, 5, { lang: "fa", status: "rejected" });
    expect(offered(store).find((c) => c.id === "geo")!.langs).toEqual([]);
  });

  test("questions of other kinds do not count", () => {
    const { store, add } = bank();
    addMany(add, MIN - 1, { lang: "fa" });
    addMany(add, 5, { lang: "fa", kind: "poll_choice" });
    expect(offered(store).find((c) => c.id === "geo")!.langs).toEqual([]);
  });

  test("the minimum is counted per language and per category", () => {
    const { store, add } = bank();
    addMany(add, MIN, { lang: "en" });
    addMany(add, 5, { lang: "fa" });
    addMany(add, 5, { lang: "en", categoryId: "sport" });
    const listed = offered(store);
    expect(listed.find((c) => c.id === "geo")!.langs).toEqual(["en"]);
    expect(listed.find((c) => c.id === "sport")!.langs).toEqual([]);
  });
});
