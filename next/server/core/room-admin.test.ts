import { afterEach, describe, expect, test } from "bun:test";
import { Elysia } from "elysia";
import { testApp } from "../../tests/support/app";
import { defineFeature, type FeatureContext } from "./feature";
import type { RoomType } from "./rooms";
import { lobbyRoutes } from "./rooms.routes";

let app: ReturnType<typeof testApp>;
afterEach(() => app.cleanup());

/** A game that only knows whether an administrator ended it. */
let endedByAdmin = 0;
const game = defineFeature({
  name: "game",
  server: (context: FeatureContext) => {
    const type: RoomType<object> = {
      format: "game", maxCapacity: 4, lobbyLifetimeMs: 60_000, lobbyEnding: "lobby_expired",
      start: (_room, changed) => {
        let over = false;
        return { ok: true, game: { leave: () => false, ended: () => (over ? { reason: "ended_by_admin" } : null), end: () => { endedByAdmin++; over = true; changed(); }, view: () => ({}), snapshot: () => null } };
      },
    };
    return new Elysia({ prefix: "/f/game" }).use(lobbyRoutes({ context, rooms: context.rooms, type, segment: "play", readSettings: () => ({}) }));
  },
});

async function twoRooms() {
  app = testApp([game]);
  const ann = await app.signIn("ann@example.com");
  const lobby = (await app.call("POST", "/f/game/play/lobby", { capacity: 4 }, ann)).body;
  app.scheduler.advanceTo(Date.parse("2026-11-03T12:00:01Z"));
  const running = (await app.call("POST", "/f/game/play/lobby", { capacity: 4 }, ann)).body;
  const bob = await app.signIn("bob@example.com");
  await app.call("POST", `/f/game/play/join/${running.code}`, undefined, bob);
  return { ann, lobby, running, admin: await app.signInAdmin() };
}

describe("admin rooms", () => {
  test("lists open lobbies and running games, oldest first", async () => {
    const { lobby, running, admin } = await twoRooms();

    expect((await app.call("GET", "/api/admin/rooms", undefined, admin)).body).toMatchObject([
      { id: lobby.id, code: lobby.code, format: "game", phase: "lobby", participants: [{ displayName: "ann" }] },
      { id: running.id, phase: "lobby", createdAt: Date.parse("2026-11-03T12:00:01Z") },
    ]);
  });

  test("a running game is listed with its players", async () => {
    const { ann, running, admin } = await twoRooms();
    await app.call("POST", `/f/game/play/${running.id}/start`, undefined, ann);

    const listed = (await app.call("GET", "/api/admin/rooms", undefined, admin)).body.find((room: { id: string }) => room.id === running.id);

    expect(listed).toMatchObject({ phase: "running", participants: [{ displayName: "ann" }, { displayName: "bob" }] });
  });

  test("ending a lobby closes it; ending a running game asks the game to end; both leave the list", async () => {
    const { ann, lobby, running, admin } = await twoRooms();
    await app.call("POST", `/f/game/play/${running.id}/start`, undefined, ann);
    endedByAdmin = 0;

    expect((await app.call("POST", `/api/admin/rooms/${lobby.id}/end`, undefined, admin)).status).toBe(200);
    expect((await app.call("POST", `/api/admin/rooms/${running.id}/end`, undefined, admin)).status).toBe(200);

    expect(endedByAdmin).toBe(1);
    expect((await app.call("GET", "/api/admin/rooms", undefined, admin)).body).toEqual([]);
    expect((await app.call("POST", `/api/admin/rooms/${lobby.id}/end`, undefined, admin)).status).toBe(404);
  });
});

describe("ended rooms", () => {
  test("stay readable for a day after they end, then are forgotten", async () => {
    const { ann, lobby, admin } = await twoRooms();
    await app.call("POST", `/api/admin/rooms/${lobby.id}/end`, undefined, admin);
    const endedAt = Date.parse("2026-11-03T12:00:01Z");

    app.scheduler.advanceTo(endedAt + 24 * 3600_000 - 1);
    expect((await app.call("GET", `/f/game/play/${lobby.id}`, undefined, ann)).body).toMatchObject({ phase: "ended" });
    app.scheduler.advanceTo(endedAt + 24 * 3600_000);
    expect((await app.call("GET", `/f/game/play/${lobby.id}`, undefined, ann)).status).toBe(404);
  });
});
