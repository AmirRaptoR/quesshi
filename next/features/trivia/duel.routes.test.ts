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

    expect(result).toEqual({
      slot: 0, questionId: expect.any(String), points: 80, correct: true, reveal: { correctIndex: 0, correctOption: "A" }, explanation: null, runFinished: false, runScore: 80,
    });
  });

  test("the served question carries its id and category, so a player can report it after answering", async () => {
    const { id, ann } = await started();
    const card = (await app.call("POST", `/f/trivia/duel/${id}/next`, undefined, ann)).body;
    const result = (await app.call("POST", `/f/trivia/duel/${id}/answer`, { slot: 0, answer: { index: 1 } }, ann)).body;

    expect(card.question).toMatchObject({ id: result.questionId, categoryId: "geo" });
    expect((await app.call("POST", `/api/questions/${result.questionId}/report`, { reason: "unclear" }, ann)).body).toEqual({ reported: true, alreadyReported: false });
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

describe("async duel review", () => {
  test("a player reviews every question with the answers given, once their own run is finished", async () => {
    const { id, ann, bob } = await started();
    const annId = (await app.call("GET", "/api/me", undefined, ann)).body.id;
    const bobId = (await app.call("GET", "/api/me", undefined, bob)).body.id;
    expect((await app.call("GET", `/f/trivia/duel/${id}`, undefined, ann)).body.review).toBeNull();

    await playRun(id, ann);
    await app.call("POST", `/f/trivia/duel/${id}/next`, undefined, bob);
    await app.call("POST", `/f/trivia/duel/${id}/answer`, { slot: 0, answer: { index: 3 } }, bob);

    const review = (await app.call("GET", `/f/trivia/duel/${id}`, undefined, ann)).body.review;
    expect(review).toHaveLength(10);
    expect(review[0]).toMatchObject({
      slot: 0, questionId: expect.any(String), categoryId: "geo", kind: "choice", explanation: null,
      card: { options: ["A", "B", "C", "D"] }, reveal: { correctIndex: 0 },
      answers: [{ player: annId, value: { index: 0 }, correct: true }, { player: bobId, value: { index: 3 }, correct: false }],
    });
    expect(review[1].answers).toEqual([{ player: annId, value: { index: 0 }, correct: true }]);
    expect((await app.call("GET", `/f/trivia/duel/${id}`, undefined, bob)).body.review).toBeNull();
  });
});

describe("a player's own duels", () => {
  test("lists the duels a player sits in, open lobbies and started games alike, newest first", async () => {
    const { id, ann, bob } = await started();
    app.scheduler.advanceTo(T0 + 60_000);
    const open = (await app.call("POST", "/f/trivia/duel/lobby", settings, ann)).body;
    await app.call("POST", "/f/trivia/live/lobby", settings, ann);

    const mine = (await app.call("GET", "/f/trivia/duels", undefined, ann)).body;
    const theirs = (await app.call("GET", "/f/trivia/duels", undefined, bob)).body;

    expect(mine.map((d: { id: string; phase: string }) => [d.id, d.phase])).toEqual([[open.id, "lobby"], [id, "running"]]);
    expect(theirs.map((d: { id: string }) => d.id)).toEqual([id]);
    expect(theirs[0]).toMatchObject({ code: expect.any(String), total: 10, runs: expect.any(Array), participants: expect.any(Array) });
    expect((await app.call("GET", "/f/trivia/duels")).status).toBe(401);
  });
});

describe("a game code", () => {
  test("leads to its room and format, and an unknown code to nothing", async () => {
    app = testApp([trivia]);
    const ann = await app.signIn("ann@example.com");
    const live = (await app.call("POST", "/f/trivia/live/lobby", settings, ann)).body;
    const duel = (await app.call("POST", "/f/trivia/duel/lobby", settings, ann)).body;

    expect((await app.call("GET", `/f/trivia/code/${live.code.toLowerCase()}`)).body).toEqual({ id: live.id, format: "live" });
    expect((await app.call("GET", `/f/trivia/code/${duel.code}`)).body).toEqual({ id: duel.id, format: "async_duel" });
    expect((await app.call("GET", "/f/trivia/code/ZZZZZZ")).status).toBe(404);
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
