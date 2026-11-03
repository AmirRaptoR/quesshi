import { afterEach, describe, expect, test } from "bun:test";
import { T0, testApp } from "../../tests/support/app";
import trivia from ".";

let app: ReturnType<typeof testApp>;
afterEach(() => app.cleanup());

const settings = { lang: "en", questions: 10, categoryIds: [], levels: [], capacity: 3 };

async function lobby() {
  app = testApp([trivia]);
  app.seedChoiceQuestions();
  const owner = await app.signIn("owner@example.com");
  const created = await app.call("POST", "/f/trivia/live/lobby", settings, owner);
  return { owner, id: created.body.id as string, code: created.body.code as string };
}

describe("live lobby", () => {
  test("an owner opens a lobby with a six-character code that avoids look-alike characters", async () => {
    const { owner, id, code } = await lobby();

    expect(code).toMatch(/^[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{6}$/);
    expect((await app.call("GET", `/f/trivia/live/${id}`, undefined, owner)).body).toMatchObject({
      code, capacity: 3, phase: "lobby", participants: [{ displayName: "owner", guest: false }], lobbyEndsAt: T0 + 10 * 60_000,
    });
  });

  test("a guest joins by code with a name, and a signed-in player joins by code", async () => {
    const { owner, id, code } = await lobby();

    const guest = await app.call("POST", `/f/trivia/live/guest/${code}`, { name: "Dina", lang: "en" });
    const bo = await app.signIn("bo@example.com");
    await app.call("POST", `/f/trivia/live/join/${code}`, undefined, bo);

    expect(guest.cookie).toBeDefined();
    const view = (await app.call("GET", `/f/trivia/live/${id}`, undefined, owner)).body;
    expect(view.participants.map((p: { displayName: string; guest: boolean }) => [p.displayName, p.guest])).toEqual([["owner", false], ["Dina", true], ["bo", false]]);
  });

  test("a full lobby and a started game refuse new players", async () => {
    const { owner, id, code } = await lobby();
    await app.call("POST", `/f/trivia/live/join/${code}`, undefined, await app.signIn("bo@example.com"));
    await app.call("POST", `/f/trivia/live/join/${code}`, undefined, await app.signIn("cy@example.com"));

    expect((await app.call("POST", `/f/trivia/live/join/${code}`, undefined, await app.signIn("dee@example.com"))).body).toEqual({ error: "full" });
    await app.call("POST", `/f/trivia/live/${id}/start`, undefined, owner);
    await app.call("POST", `/f/trivia/live/${id}/leave`, undefined, owner);
    expect((await app.call("POST", `/f/trivia/live/guest/${code}`, { name: "Eve" })).body).toEqual({ error: "started" });
  });

  test("only the owner starts, with at least two players", async () => {
    const { owner, id, code } = await lobby();
    expect((await app.call("POST", `/f/trivia/live/${id}/start`, undefined, owner)).body).toEqual({ error: "cannot_start" });
    const bo = await app.signIn("bo@example.com");
    await app.call("POST", `/f/trivia/live/join/${code}`, undefined, bo);

    expect((await app.call("POST", `/f/trivia/live/${id}/start`, undefined, bo)).body).toEqual({ error: "cannot_start" });
    expect((await app.call("POST", `/f/trivia/live/${id}/start`, undefined, owner)).status).toBe(200);
    expect((await app.call("GET", `/f/trivia/live/${id}`, undefined, owner)).body.game).toMatchObject({ phase: "countdown", total: 10 });
  });

  test("starting fails clearly when the bank has too few questions", async () => {
    app = testApp([trivia]);
    app.seedChoiceQuestions(3);
    const owner = await app.signIn("owner@example.com");
    const { id, code } = (await app.call("POST", "/f/trivia/live/lobby", settings, owner)).body;
    await app.call("POST", `/f/trivia/live/join/${code}`, undefined, await app.signIn("bo@example.com"));

    const response = await app.call("POST", `/f/trivia/live/${id}/start`, undefined, owner);

    expect(response.status).toBe(503);
    expect(response.body).toEqual({ error: "not_enough_questions" });
  });

  test("a player may leave the lobby; the owner cancels it instead", async () => {
    const { owner, id, code } = await lobby();
    const bo = await app.signIn("bo@example.com");
    await app.call("POST", `/f/trivia/live/join/${code}`, undefined, bo);

    await app.call("POST", `/f/trivia/live/${id}/leave`, undefined, bo);
    expect((await app.call("GET", `/f/trivia/live/${id}`, undefined, owner)).body.participants).toHaveLength(1);

    await app.call("DELETE", `/f/trivia/live/${id}`, undefined, owner);
    expect((await app.call("GET", `/f/trivia/live/${id}`, undefined, owner)).body).toMatchObject({ phase: "ended", endedReason: "owner_cancelled" });
  });

  test("an unstarted lobby expires after ten minutes", async () => {
    const { owner, id } = await lobby();

    app.scheduler.advanceTo(T0 + 10 * 60_000);

    expect((await app.call("GET", `/f/trivia/live/${id}`, undefined, owner)).body).toMatchObject({ phase: "ended", endedReason: "lobby_expired" });
  });

  test("guests cannot open lobbies", async () => {
    const { code } = await lobby();
    const guest = (await app.call("POST", `/f/trivia/live/guest/${code}`, { name: "Dina" })).cookie;

    expect((await app.call("POST", "/f/trivia/live/lobby", settings, guest)).status).toBe(403);
  });
});

describe("live game over WebSocket", () => {
  async function playing() {
    const { owner, id, code } = await lobby();
    const bo = await app.signIn("bo@example.com");
    await app.call("POST", `/f/trivia/live/join/${code}`, undefined, bo);
    const server = app.listen();
    const ann = server.socket(`/f/trivia/live/${id}/socket`, owner);
    const bob = server.socket(`/f/trivia/live/${id}/socket`, bo);
    await Promise.all([ann.opened, bob.opened]);
    return { owner, id, server, ann, bob };
  }

  test("every phone gets the view on connect and each change after it", async () => {
    const { owner, id, server, ann, bob } = await playing();
    await ann.next((m) => m.type === "view" && m.view.phase === "lobby");

    await app.call("POST", `/f/trivia/live/${id}/start`, undefined, owner);
    app.scheduler.advanceTo(T0 + 3_000);

    await bob.next((m) => m.type === "view" && m.view.game?.phase === "question");
    server.close();
  });

  test("an answer is acknowledged, a malformed one refused, and everyone sees the reveal", async () => {
    const { owner, id, server, ann, bob } = await playing();
    await app.call("POST", `/f/trivia/live/${id}/start`, undefined, owner);
    app.scheduler.advanceTo(T0 + 3_000);

    ann.send({ type: "answer", slot: 0, answer: { index: 7 } });
    expect(await ann.next((m) => m.type === "error")).toEqual({ type: "error", error: "malformed_answer" });
    ann.send({ type: "answer", slot: 0, answer: { index: 0 } });
    expect(await ann.next((m) => m.type === "answered")).toEqual({ type: "answered", slot: 0, answer: { index: 0 } });
    bob.send({ type: "answer", slot: 0, answer: { index: 1 } });

    const reveal = await bob.next((m) => m.type === "view" && m.view.game?.phase === "reveal");
    expect(reveal.view.game.reveal.answers.map((a: { points: number }) => a.points)).toEqual([80, 0]); // the first question is level 1, so 160 × 0.5
    server.close();
  });

  test("a socket from another site is refused", async () => {
    const { id, server, owner } = await (async () => {
      const { owner, id } = await lobby();
      return { owner, id, server: app.listen() };
    })();

    const socket = server.socket(`/f/trivia/live/${id}/socket`, owner, "https://evil.example.net");

    expect(socket.opened).rejects.toThrow();
    server.close();
  });
});

describe("lobby settings", () => {
  test("the owner changes settings and capacity before the start; nobody else can", async () => {
    const { owner, id, code } = await lobby();
    const bo = await app.signIn("bo@example.com");
    await app.call("POST", `/f/trivia/live/join/${code}`, undefined, bo);
    const changed = { lang: "fa", questions: 20, categoryIds: ["geo"], levels: [2, 4], capacity: 5 };

    expect((await app.call("PUT", `/f/trivia/live/${id}/settings`, changed, bo)).body).toEqual({ error: "cannot_update_settings" });
    expect((await app.call("PUT", `/f/trivia/live/${id}/settings`, changed, owner)).status).toBe(200);

    expect((await app.call("GET", `/f/trivia/live/${id}`, undefined, owner)).body).toMatchObject({
      capacity: 5, settings: { lang: "fa", questions: 20, categoryIds: ["geo"], levels: [2, 4] },
    });
  });

  test("capacity cannot drop below the players already in", async () => {
    const { owner, id, code } = await lobby();
    await app.call("POST", `/f/trivia/live/join/${code}`, undefined, await app.signIn("bo@example.com"));
    await app.call("POST", `/f/trivia/live/join/${code}`, undefined, await app.signIn("cy@example.com"));

    expect((await app.call("PUT", `/f/trivia/live/${id}/settings`, { ...settings, capacity: 2 }, owner)).body).toEqual({ error: "cannot_update_settings" });
  });
});

describe("rematch", () => {
  async function finished() {
    const { owner, id, code } = await lobby();
    const bo = await app.signIn("bo@example.com");
    await app.call("POST", `/f/trivia/live/join/${code}`, undefined, bo);
    await app.call("POST", `/f/trivia/live/${id}/start`, undefined, owner);
    await app.call("POST", `/f/trivia/live/${id}/leave`, undefined, owner); // one player left: the game completes
    return { owner, bo, id };
  }

  test("a player opens one rematch lobby, owned by the original owner with the same settings", async () => {
    const { owner, bo, id } = await finished();

    const first = await app.call("POST", `/f/trivia/live/${id}/rematch`, undefined, bo);
    const again = await app.call("POST", `/f/trivia/live/${id}/rematch`, undefined, owner);

    expect(again.body).toEqual(first.body);
    expect((await app.call("GET", `/f/trivia/live/${first.body.id}`, undefined, owner)).body).toMatchObject({
      phase: "lobby", code: first.body.code, capacity: 3, settings: { lang: "en", questions: 10 }, participants: [{ displayName: "owner" }],
    });
    expect((await app.call("GET", `/f/trivia/live/${id}`, undefined, owner)).body.rematch).toEqual(first.body);
  });

  test("a game still running and a guest cannot ask for a rematch", async () => {
    const { owner, id, code } = await lobby();
    const guest = (await app.call("POST", `/f/trivia/live/guest/${code}`, { name: "Dina" })).cookie;
    await app.call("POST", `/f/trivia/live/${id}/start`, undefined, owner);

    expect((await app.call("POST", `/f/trivia/live/${id}/rematch`, undefined, owner)).body).toEqual({ error: "refused" });
    await app.call("POST", `/f/trivia/live/${id}/leave`, undefined, owner);
    expect((await app.call("POST", `/f/trivia/live/${id}/rematch`, undefined, guest)).body).toEqual({ error: "refused" });
  });
});
