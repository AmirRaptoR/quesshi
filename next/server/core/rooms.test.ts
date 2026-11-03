import { afterEach, describe, expect, test } from "bun:test";
import { fakeScheduler, seededRandom } from "../../tests/support/time";
import { tempWorld } from "../../tests/support/temp";
import { createRooms, type RoomType } from "./rooms";
import { createSessionsStore } from "./sessions.store";

let world: ReturnType<typeof tempWorld>;
afterEach(() => world.cleanup());

const nextTurn = () => new Promise((resolve) => setTimeout(resolve, 0));

function setUp() {
  world = tempWorld();
  world.migrateCore();
  const published: { topic: string; message: { type: string; view: Record<string, unknown> } }[] = [];
  const scheduler = fakeScheduler(Date.parse("2026-11-03T12:00:00Z"));
  const rooms = createRooms({
    clock: scheduler.clock, schedule: scheduler.schedule, random: seededRandom(),
    player: (id) => ({ id, email: null, displayName: id, avatarSeed: "avatar-0", lang: "en", guest: false, banned: false }),
    publish: (topic, message) => published.push({ topic, message: message as (typeof published)[number]["message"] }),
  }, createSessionsStore(world.db));
  const type: RoomType<object> = {
    format: "game", maxCapacity: 600, lobbyLifetimeMs: 60_000, lobbyEnding: "lobby_expired",
    start: () => ({ ok: true, game: { leave: () => false, ended: () => null, end: () => {}, view: (_viewer, full) => ({ game: full ? "everything" : "the little that changed" }) } }),
  };
  return { rooms, type, published };
}

describe("room broadcasts", () => {
  test("changes made in one turn go out as one message, with the latest view", async () => {
    const { rooms, type, published } = setUp();
    const { id, code } = rooms.open(type, "ann", {}, 600);
    for (const player of ["bob", "cy", "dee"]) rooms.join(code, "game", player);

    await nextTurn();

    expect(published).toHaveLength(1);
    expect((published[0]!.message.view.participants as unknown[]).length).toBe(4);
    expect(published[0]!.topic).toBe(rooms.topic(id));
  });

  test("once a game runs, broadcasts leave out the roster and carry the game's short view; a reader gets everything", async () => {
    const { rooms, type, published } = setUp();
    const { id, code } = rooms.open(type, "ann", {}, 600);
    rooms.join(code, "game", "bob");
    rooms.start(id, "game", "ann");

    await nextTurn();

    const broadcast = published.at(-1)!.message.view;
    expect(broadcast).toMatchObject({ phase: "running", game: "the little that changed" });
    expect(broadcast).not.toHaveProperty("participants");
    expect(rooms.view(id, "game", "ann")).toMatchObject({ participants: [{ id: "ann" }, { id: "bob" }], game: "everything" });
  });
});
