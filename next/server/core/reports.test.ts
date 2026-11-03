import { afterEach, describe, expect, test } from "bun:test";
import { T0, testApp } from "../../tests/support/app";
import { createSessionsStore } from "./sessions.store";

let app: ReturnType<typeof testApp>;
afterEach(() => app.cleanup());

/** Three players who each played a session that served q0. */
async function played() {
  app = testApp([]);
  app.seedChoiceQuestions(2);
  const sessions = createSessionsStore(app.world.db);
  const players: { cookie: string; id: string }[] = [];
  for (const name of ["ann", "bob", "cy"]) {
    const cookie = await app.signIn(`${name}@example.com`);
    players.push({ cookie, id: (await app.call("GET", "/api/me", undefined, cookie)).body.id });
  }
  sessions.createLobby({ id: "s1", code: "ABCDEF", format: "live", createdAt: new Date(T0).toISOString() });
  sessions.start("s1", players.map((p) => p.id), ["q0"]);
  return players;
}
const report = (cookie: string, question: string, reason = "wrong_answer") => app.call("POST", `/api/questions/${question}/report`, { reason }, cookie);
const status = (id: string) => app.questions.question(id)?.status;

describe("reporting a question", () => {
  test("a player reports a question they were served, once", async () => {
    const [ann] = await played();

    expect((await report(ann!.cookie, "q0")).body).toEqual({ reported: true, alreadyReported: false });
    expect((await report(ann!.cookie, "q0", "unclear")).body).toEqual({ reported: true, alreadyReported: true });
  });

  test("refuses an unknown reason and a question the player was never served", async () => {
    const [ann] = await played();

    expect((await report(ann!.cookie, "q0", "boring")).body).toEqual({ error: "bad_reason" });
    expect((await report(ann!.cookie, "q1")).body).toEqual({ error: "not_your_question" });
  });

  test("the third report takes an approved question out of play", async () => {
    const players = await played();
    await report(players[0]!.cookie, "q0");
    await report(players[1]!.cookie, "q0");
    expect(status("q0")).toBe("approved");

    await report(players[2]!.cookie, "q0", "offensive");

    expect(status("q0")).toBe("pending");
  });
});

describe("reported questions in the admin", () => {
  test("lists reported questions with who reported them and why, then dismissing clears them and approves", async () => {
    const [ann, bob] = await played();
    await report(ann!.cookie, "q0");
    app.scheduler.advanceTo(T0 + 1000);
    await report(bob!.cookie, "q0", "duplicate");
    const admin = await app.signInAdmin();

    expect((await app.call("GET", "/api/admin/reported", undefined, admin)).body).toMatchObject({
      total: 1,
      items: [{
        id: "q0", reports: [
          { playerId: ann!.id, displayName: "ann", reason: "wrong_answer", at: new Date(T0).toISOString() },
          { playerId: bob!.id, displayName: "bob", reason: "duplicate", at: new Date(T0 + 1000).toISOString() },
        ],
      }],
    });

    await app.call("POST", "/api/admin/questions/q0/dismiss-reports", undefined, admin);

    expect((await app.call("GET", "/api/admin/reported", undefined, admin)).body).toEqual({ total: 0, items: [] });
    expect(status("q0")).toBe("approved");
  });
});
