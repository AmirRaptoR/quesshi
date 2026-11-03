import { afterEach, describe, expect, test } from "bun:test";
import { testApp } from "../../tests/support/app";
import { createQuestionSetsStore, MAX_QUESTIONS_PER_SET, MAX_SETS_PER_PLAYER, type SetQuestionInput } from "./question-sets.store";

let app: ReturnType<typeof testApp>;
afterEach(() => app.cleanup());

const NOW = "2026-11-03T12:00:00Z";
const question = (id: string): SetQuestionInput => ({ id, kind: "choice", level: null, prompt: `Q ${id}?`, payload: { options: ["A", "B", "C", "D"], correctIndex: 0 }, explanation: null });
const store = () => createQuestionSetsStore(app.world.db);
const playerId = async (cookie: string) => (await app.call("GET", "/api/me", undefined, cookie)).body.id as string;
const create = (cookie: string, name = "Mine", lang = "en") => app.call("POST", "/api/question-sets", { name, lang }, cookie);

/** Two signed-in players, each with one set holding one question. */
async function twoOwners() {
  app = testApp([]);
  const ann = await app.signIn("ann@example.com");
  const bob = await app.signIn("bob@example.com");
  const set = (await create(ann, "Ann's")).body.id as string;
  store().append(set, [question("annq")], NOW);
  return { ann, bob, set };
}

describe("question set management", () => {
  test("a signed-in player creates a set and sees it listed", async () => {
    app = testApp([]);
    const ann = await app.signIn("ann@example.com");

    const created = await create(ann, "Movies", "nl");
    const listed = await app.call("GET", "/api/question-sets", undefined, ann);

    expect(created.status).toBe(200);
    expect(listed.body).toMatchObject([{ id: created.body.id, name: "Movies", lang: "nl", questionCount: 0 }]);
  });

  test("a player renames their set", async () => {
    const { ann, set } = await twoOwners();

    const renamed = await app.call("PATCH", `/api/question-sets/${set}`, { name: "Renamed" }, ann);

    expect(renamed.body).toMatchObject({ id: set, name: "Renamed" });
    expect((await app.call("GET", "/api/question-sets", undefined, ann)).body[0].name).toBe("Renamed");
  });

  test("deleting a set removes its questions", async () => {
    const { ann, set } = await twoOwners();

    expect((await app.call("DELETE", `/api/question-sets/${set}`, undefined, ann)).status).toBe(200);

    expect((await app.call("GET", "/api/question-sets", undefined, ann)).body).toEqual([]);
    expect(app.questions.question("annq")).toBeNull();
  });

  test("a guest cannot create a set", async () => {
    app = testApp([]);
    app.world.db.run("insert into players (id, email, display_name, avatar_seed, lang, guest, created_at) values ('g1', null, 'Dina', 'g1', 'en', 1, ?)", [NOW]);
    app.world.db.run("insert into auth_sessions (token_hash, player_id, created_at, expires_at) values (?, 'g1', ?, '2027-12-03T12:00:00Z')",
      [new Bun.CryptoHasher("sha256").update("guest-token").digest("hex"), NOW]);

    const refused = await create("__Host-quesshi=guest-token");

    expect(refused.status).toBe(403);
    expect(store().list("g1")).toEqual([]);
  });

  test("a player cannot create a 21st set", async () => {
    app = testApp([]);
    const ann = await app.signIn("ann@example.com");
    for (let i = 0; i < MAX_SETS_PER_PLAYER; i++) expect((await create(ann, `Set ${i}`)).status).toBe(200);

    const refused = await create(ann, "One too many");

    expect(refused).toMatchObject({ status: 400, body: { error: "too_many_sets" } });
    expect((await app.call("GET", "/api/question-sets", undefined, ann)).body).toHaveLength(MAX_SETS_PER_PLAYER);
  });

  test("the set limit is per player", async () => {
    const { bob } = await twoOwners();
    expect((await create(bob)).status).toBe(200);
  });
});

describe("the core append function", () => {
  test("keeps the first 100 questions and refuses the rest", async () => {
    app = testApp([]);
    const ann = await app.signIn("ann@example.com");
    const set = (await create(ann)).body.id as string;
    store().append(set, Array.from({ length: 98 }, (_, i) => question(`a${i}`)), NOW);

    const result = store().append(set, ["b0", "b1", "b2", "b3"].map(question), NOW);

    expect(result).toEqual({ added: 2, refused: 2 });
    const held = store().questions(set).map((q) => q.id);
    expect(held).toHaveLength(MAX_QUESTIONS_PER_SET);
    expect(held.slice(-2)).toEqual(["b0", "b1"]);
    expect(store().append(set, [question("late")], NOW)).toEqual({ added: 0, refused: 1 });
  });
});

describe("other players' sets", () => {
  test("a player cannot read another player's set or its questions", async () => {
    const { bob, set } = await twoOwners();

    expect((await app.call("GET", `/api/question-sets/${set}/questions`, undefined, bob)).status).toBe(404);
    expect((await app.call("GET", "/api/question-sets", undefined, bob)).body).toEqual([]);
  });

  test("a player cannot rename or delete another player's set, or a question in it", async () => {
    const { ann, bob, set } = await twoOwners();

    expect((await app.call("PATCH", `/api/question-sets/${set}`, { name: "Stolen" }, bob)).status).toBe(404);
    expect((await app.call("DELETE", `/api/question-sets/${set}/questions/annq`, undefined, bob)).status).toBe(404);
    expect((await app.call("DELETE", `/api/question-sets/${set}`, undefined, bob)).status).toBe(404);

    expect((await app.call("GET", "/api/question-sets", undefined, ann)).body).toMatchObject([{ name: "Ann's", questionCount: 1 }]);
  });

  test("a player deletes a single question from their own set", async () => {
    const { ann, set } = await twoOwners();
    store().append(set, [question("annq2")], NOW);

    expect((await app.call("DELETE", `/api/question-sets/${set}/questions/annq`, undefined, ann)).status).toBe(200);

    expect((await app.call("GET", `/api/question-sets/${set}/questions`, undefined, ann)).body.map((q: { id: string }) => q.id)).toEqual(["annq2"]);
  });
});

describe("private set questions", () => {
  test("never come out of the shared category draws", async () => {
    const { set } = await twoOwners();
    app.seedChoiceQuestions(1);
    const draw = (exclude: string[]) => app.questions.randomApproved({ lang: "en", kinds: ["choice"], categoryIds: null, level: null, exclude });
    expect(draw([])?.id).toBe("q0");
    expect(draw(["q0"])).toBeNull();
    expect(store().draw(set, { kinds: ["choice"], exclude: [] })?.id).toBe("annq");
    expect(store().draw(set, { kinds: ["choice"], exclude: ["annq"] })).toBeNull();
  });

  test("do not appear in the admin question list", async () => {
    await twoOwners();
    app.seedChoiceQuestions(1);
    const admin = await app.signInAdmin();

    const listed = (await app.call("GET", "/api/admin/questions", undefined, admin)).body;

    expect(listed.items.map((q: { id: string }) => q.id)).toEqual(["q0"]);
    expect(listed.total).toBe(1);
  });
});
