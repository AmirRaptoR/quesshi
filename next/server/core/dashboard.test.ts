import { afterEach, describe, expect, test } from "bun:test";
import { T0, testApp } from "../../tests/support/app";
import { createSessionsStore } from "./sessions.store";

let app: ReturnType<typeof testApp>;
afterEach(() => app.cleanup());

describe("admin dashboard", () => {
  test("counts members, questions by status, finished sessions and open rooms", async () => {
    app = testApp([]);
    app.seedChoiceQuestions(3);
    app.world.db.run("update questions set status = 'pending' where id = 'q0'");
    await app.signIn("ann@example.com");
    await app.signIn("bob@example.com");
    const sessions = createSessionsStore(app.world.db);
    sessions.createLobby({ id: "s1", code: "ABCDEF", format: "live", createdAt: new Date(T0).toISOString() });
    sessions.finish({ sessionId: "s1", endedAt: new Date(T0).toISOString(), reason: "completed", result: null });
    sessions.createLobby({ id: "s2", code: "BCDEFG", format: "live", createdAt: new Date(T0).toISOString() });

    const dashboard = (await app.call("GET", "/api/admin/dashboard", undefined, await app.signInAdmin())).body;

    expect(dashboard).toEqual({ players: 2, questions: { total: 3, approved: 2, pending: 1, rejected: 0 }, sessionsPlayed: 1, openRooms: 0 });
  });
});
