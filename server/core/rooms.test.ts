import { afterEach, describe, expect, test } from "bun:test";
import { fakeScheduler, seededRandom } from "../../tests/support/time";
import { tempWorld } from "../../tests/support/temp";
import { createRooms, type RoomType } from "./rooms";
import { createRoomsStore } from "./rooms.store";
import { createSessionsStore } from "./sessions.store";

let world: ReturnType<typeof tempWorld>;
afterEach(async () => {
  await nextTurn();
  world.cleanup();
});

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
  }, createSessionsStore(world.db), createRoomsStore(world.db));
  const type: RoomType<object> = {
    format: "game", maxCapacity: 600, lobbyLifetimeMs: 60_000, lobbyEnding: "lobby_expired",
    start: () => ({ ok: true, game: { leave: () => false, ended: () => null, end: () => {}, snapshot: () => null, view: (_viewer, full) => ({ game: full ? "everything" : "the little that changed" }) } }),
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

describe("rooms across a restart", () => {
  /** A game whose whole state is a counter, saved in its snapshot. */
  function counterType(resumable: boolean): RoomType<{ lang: string }> {
    const handle = (count: { n: number }, changed: () => void) => ({
      leave: () => { count.n++; changed(); return true; }, ended: () => null, end: () => {},
      view: () => ({ game: { count: count.n } }), snapshot: () => ({ count: count.n }),
    });
    return {
      format: "counter", maxCapacity: 10, lobbyLifetimeMs: 60_000, lobbyEnding: "lobby_expired",
      start: (_room, changed) => ({ ok: true, game: handle({ n: 0 }, changed) }),
      ...(resumable ? { resume: (_room: unknown, saved: unknown, changed: () => void) => handle({ n: (saved as { count: number }).count }, changed) } : {}),
    };
  }

  function restart(rooms: ReturnType<typeof setUp>["rooms"]) {
    void rooms;
    const scheduler = fakeScheduler(Date.parse("2026-11-03T12:00:10Z"));
    const fresh = createRooms({
      clock: scheduler.clock, schedule: scheduler.schedule, random: seededRandom(2),
      player: (id) => ({ id, email: null, displayName: id, avatarSeed: "avatar-0", lang: "en", guest: false, banned: false }),
      publish: () => {},
    }, createSessionsStore(world.db), createRoomsStore(world.db));
    return { rooms: fresh, scheduler };
  }

  test("an open lobby comes back with its code, owner, players and settings, and still expires on time", async () => {
    const { rooms } = setUp();
    const type = counterType(true);
    const { id, code } = rooms.open(type, "ann", { lang: "fa" }, 4);
    rooms.join(code, "counter", "bob");
    await nextTurn();

    const after = restart(rooms);
    after.rooms.restore(type);

    expect(after.rooms.view(id, "counter", "ann")).toMatchObject({ code, owner: "ann", capacity: 4, settings: { lang: "fa" }, phase: "lobby", participants: [{ id: "ann" }, { id: "bob" }] });
    expect(after.rooms.join(code, "counter", "cy")).toEqual({ ok: true, id });
    after.scheduler.advanceTo(Date.parse("2026-11-03T12:01:00Z"));
    expect(after.rooms.view(id, "counter", "ann")).toMatchObject({ phase: "ended", endedReason: "lobby_expired" });
  });

  test("a running game is handed its snapshot and carries on", async () => {
    const { rooms } = setUp();
    const type = counterType(true);
    const { id, code } = rooms.open(type, "ann", { lang: "fa" }, 4);
    rooms.join(code, "counter", "bob");
    rooms.start(id, "counter", "ann");
    rooms.room(id, "counter")!.game!.leave("bob");
    await nextTurn();

    const after = restart(rooms);
    after.rooms.restore(type);

    expect(after.rooms.view(id, "counter", "ann")).toMatchObject({ phase: "running", game: { count: 1 } });
  });

  test("a game its feature cannot resume ends as no contest, and ended rooms are not brought back", async () => {
    const { rooms } = setUp();
    const type = counterType(false);
    const running = rooms.open(type, "ann", { lang: "fa" }, 4);
    rooms.join(running.code, "counter", "bob");
    rooms.start(running.id, "counter", "ann");
    const cancelled = rooms.open(type, "cy", { lang: "fa" }, 4);
    rooms.cancel(cancelled.id, "counter", "cy");
    await nextTurn();

    const after = restart(rooms);
    after.rooms.restore(type);

    expect(after.rooms.view(running.id, "counter", "ann")).toMatchObject({ phase: "ended", endedReason: "server_restarted" });
    expect(world.db.query("select ended_reason from sessions where id = ?").get(running.id)).toEqual({ ended_reason: "server_restarted" });
    expect(after.rooms.view(cancelled.id, "counter", "cy")).toBeNull();
  });
});
