import { afterEach, describe, expect, test } from "bun:test";
import { Elysia } from "elysia";
import { testApp } from "../../tests/support/app";
import { defineFeature, type FeatureContext } from "./feature";
import type { RoomType } from "./rooms";
import { lobbyRoutes } from "./rooms.routes";

let app: ReturnType<typeof testApp>;
afterEach(() => app.cleanup());

const game = defineFeature({
  name: "game",
  server: (context: FeatureContext) => {
    const type: RoomType<{ questions: number }> = {
      format: "duo", maxCapacity: 2, lobbyLifetimeMs: 60_000, lobbyEnding: "lobby_expired",
      start: () => ({ ok: true, game: { leave: () => false, ended: () => null, end: () => {}, view: () => ({}) } }),
    };
    return new Elysia({ prefix: "/f/game" }).use(lobbyRoutes({ context, rooms: context.rooms, type, segment: "play", readSettings: () => ({ questions: 20 }) }));
  },
});

async function lobby() {
  app = testApp([game]);
  const ann = await app.signIn("ann@example.com");
  const room = (await app.call("POST", "/f/game/play/lobby", { capacity: 2 }, ann)).body as { id: string; code: string };
  return { ann, room };
}

describe("a room by its code", () => {
  test("names the room, its format, its owner and its settings to anyone, signed in or not", async () => {
    const { room } = await lobby();

    expect((await app.call("GET", `/api/rooms/${room.code.toLowerCase()}`)).body).toEqual({
      id: room.id, code: room.code, format: "duo", phase: "lobby", full: false, ownerName: "ann", settings: { questions: 20 },
    });
  });

  test("says when every seat is taken", async () => {
    const { room } = await lobby();
    await app.call("POST", `/f/game/play/join/${room.code}`, undefined, await app.signIn("bob@example.com"));

    expect((await app.call("GET", `/api/rooms/${room.code}`)).body).toMatchObject({ full: true });
  });

  test("an unknown or ended room is not found", async () => {
    const { ann, room } = await lobby();
    await app.call("DELETE", `/f/game/play/${room.id}`, undefined, ann);

    expect((await app.call("GET", `/api/rooms/${room.code}`)).status).toBe(404);
    expect((await app.call("GET", "/api/rooms/ZZZZZZ")).status).toBe(404);
  });
});
