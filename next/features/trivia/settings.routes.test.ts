import { afterEach, describe, expect, test } from "bun:test";
import { testApp } from "../../tests/support/app";
import trivia from ".";

let app: ReturnType<typeof testApp>;
afterEach(() => app.cleanup());

const settings = { lang: "en", questions: 10, categoryIds: [], levels: [] };

async function setUp() {
  app = testApp([trivia]);
  app.seedChoiceQuestions();
  return { admin: await app.signInAdmin(), owner: await app.signIn("owner@example.com") };
}
const save = (admin: string, body: unknown) => app.call("PUT", "/f/trivia/admin/settings", body, admin);

describe("trivia settings", () => {
  test("players read the limits; by default live is on and lobbies hold twenty", async () => {
    await setUp();

    expect((await app.call("GET", "/f/trivia/limits")).body).toEqual({ liveEnabled: true, maxCapacity: 20 });
  });

  test("only an admin changes them", async () => {
    const { owner } = await setUp();

    expect((await save(owner, { liveEnabled: false })).status).toBe(401);
  });

  test("turning live off refuses new live lobbies and joins, but not duels or reading a lobby", async () => {
    const { admin, owner } = await setUp();
    const { id, code } = (await app.call("POST", "/f/trivia/live/lobby", { ...settings, capacity: 2 }, owner)).body;

    await save(admin, { liveEnabled: false });

    expect((await app.call("POST", "/f/trivia/live/lobby", { ...settings, capacity: 2 }, owner)).body).toEqual({ error: "live_disabled" });
    expect((await app.call("POST", `/f/trivia/live/join/${code}`, undefined, await app.signIn("bo@example.com"))).status).toBe(503);
    expect((await app.call("POST", `/f/trivia/live/guest/${code}`, { name: "Eve" })).status).toBe(503);
    expect((await app.call("GET", `/f/trivia/live/${id}`, undefined, owner)).status).toBe(200);
    expect((await app.call("POST", "/f/trivia/duel/lobby", { ...settings, capacity: 2 }, owner)).status).toBe(200);
  });

  test("the largest lobby is between two and five hundred, and new lobbies follow it", async () => {
    const { admin, owner } = await setUp();

    expect((await save(admin, { maxCapacity: 1 })).body).toEqual({ error: "bad_capacity" });
    expect((await save(admin, { maxCapacity: 501 })).body).toEqual({ error: "bad_capacity" });
    expect((await save(admin, { maxCapacity: 50 })).body).toEqual({ liveEnabled: true, maxCapacity: 50 });
    expect((await app.call("POST", "/f/trivia/live/lobby", { ...settings, capacity: 50 }, owner)).status).toBe(200);
    expect((await app.call("POST", "/f/trivia/duel/lobby", { ...settings, capacity: 51 }, owner)).body).toEqual({ error: "bad_capacity" });
  });
});
