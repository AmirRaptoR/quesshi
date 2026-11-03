import { afterEach, describe, expect, test } from "bun:test";
import { T0, testApp } from "../../tests/support/app";
import trivia from ".";

let app: ReturnType<typeof testApp>;
afterEach(() => app.cleanup());

const settings = { lang: "en", questions: 10, categoryIds: [], levels: [], capacity: 2 };

/** Two players in a started duel of ten questions. */
async function started() {
  app = testApp([trivia]);
  app.seedChoiceQuestions();
  const ann = await app.signIn("ann@example.com");
  const bob = await app.signIn("bob@example.com");
  const { id, code } = (await app.call("POST", "/f/trivia/duel/lobby", settings, ann)).body;
  await app.call("POST", `/f/trivia/duel/join/${code}`, undefined, bob);
  await app.call("POST", `/f/trivia/duel/${id}/start`, undefined, ann);
  return { id, ann, bob };
}

/** Plays a whole run, answering every question correctly as soon as it is served. */
async function playRun(id: string, cookie: string) {
  for (let slot = 0; slot < 10; slot++) {
    await app.call("POST", `/f/trivia/duel/${id}/next`, undefined, cookie);
    await app.call("POST", `/f/trivia/duel/${id}/answer`, { slot, answer: { index: 0 } }, cookie);
  }
}

describe("async duel", () => {
  test("a player is served their next question with its card and clock, never its answer", async () => {
    const { id, ann } = await started();

    const card = (await app.call("POST", `/f/trivia/duel/${id}/next`, undefined, ann)).body;

    expect(card).toMatchObject({ slot: 0, total: 10, servedAt: T0, limitMs: 20_000, question: { kind: "choice", card: { options: ["A", "B", "C", "D"] } } });
    expect(JSON.stringify(card)).not.toContain("correctIndex");
  });

  test("an answer comes back with its points and the player's own reveal", async () => {
    const { id, ann } = await started();
    await app.call("POST", `/f/trivia/duel/${id}/next`, undefined, ann);

    const result = (await app.call("POST", `/f/trivia/duel/${id}/answer`, { slot: 0, answer: { index: 0 } }, ann)).body;

    expect(result).toEqual({ slot: 0, points: 80, correct: true, reveal: { correctIndex: 0, correctOption: "A" }, explanation: null, runFinished: false, runScore: 80 });
  });

  test("running out of time records a miss", async () => {
    const { id, ann } = await started();
    await app.call("POST", `/f/trivia/duel/${id}/next`, undefined, ann);

    const result = (await app.call("POST", `/f/trivia/duel/${id}/answer`, { slot: 0, answer: null }, ann)).body;

    expect(result).toMatchObject({ slot: 0, points: 0, correct: false });
    expect(app.world.db.query("select value, points from answers where slot = 0").get()).toEqual({ value: null, points: 0 });
  });

  test("a malformed answer is refused", async () => {
    const { id, ann } = await started();
    await app.call("POST", `/f/trivia/duel/${id}/next`, undefined, ann);

    expect((await app.call("POST", `/f/trivia/duel/${id}/answer`, { slot: 0, answer: { index: 9 } }, ann)).body).toEqual({ error: "malformed_answer" });
  });

  test("a player sees the other runs only once their own is finished, and standings at the end", async () => {
    const { id, ann, bob } = await started();
    await playRun(id, ann);

    const annId = (await app.call("GET", "/api/me", undefined, ann)).body.id;
    const bobId = (await app.call("GET", "/api/me", undefined, bob)).body.id;

    // Bob sees how far Ann is, but not her score, until his own run is done.
    expect((await app.call("GET", `/f/trivia/duel/${id}`, undefined, bob)).body.runs).toEqual([
      { player: annId, answered: 10, finished: true, score: null },
      { player: bobId, answered: 0, finished: false, score: 0 },
    ]);

    await playRun(id, bob);
    const view = (await app.call("GET", `/f/trivia/duel/${id}`, undefined, ann)).body;
    expect(view).toMatchObject({ phase: "ended", endedReason: "resolved" });
    expect(view.standings.map((s: { place: number; outcome: string }) => [s.place, s.outcome])).toEqual([[1, "draw"], [1, "draw"]]);
  });

  test("is forfeited 48 hours after it was created", async () => {
    const { id, ann } = await started();
    await app.call("POST", `/f/trivia/duel/${id}/next`, undefined, ann);
    await app.call("POST", `/f/trivia/duel/${id}/answer`, { slot: 0, answer: { index: 0 } }, ann);

    app.scheduler.advanceTo(T0 + 48 * 3600_000);

    expect((await app.call("GET", `/f/trivia/duel/${id}`, undefined, ann)).body).toMatchObject({ phase: "ended", endedReason: "forfeited" });
  });
});

describe("async duel, ended by an administrator", () => {
  test("ends as a no contest and serves nothing more", async () => {
    const { id, ann } = await started();
    await app.call("POST", `/f/trivia/duel/${id}/next`, undefined, ann);

    await app.call("POST", `/api/admin/rooms/${id}/end`, undefined, await app.signInAdmin());

    expect((await app.call("GET", `/f/trivia/duel/${id}`, undefined, ann)).body).toMatchObject({ phase: "ended", endedReason: "ended_by_admin" });
    expect((await app.call("POST", `/f/trivia/duel/${id}/next`, undefined, ann)).status).toBe(409);
  });
});
