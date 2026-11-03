import { describe, expect, test } from "bun:test";
import { translator } from "../../../../client/i18n";
import type { DuelRoomView } from "../types";
import { canStart, capacityStepMin, expiryText, isFull, isOwner, isParticipant, openSeatCount, rosterColumns, targetRoute } from "./lobby";

const person = (id: string) => ({ id, displayName: id, avatarSeed: id, guest: false });
const room = (fields: Partial<DuelRoomView> = {}): DuelRoomView => ({
  id: "m1", format: "live", code: "ABCDEF", owner: "amir", capacity: 4, settings: { lang: "en", questions: 10, categoryIds: [], levels: [] },
  phase: "lobby", endedReason: null, lobbyEndsAt: 0, participants: [person("amir"), person("sara")], ...fields,
});

describe("a lobby", () => {
  test("only its owner may start it, and only with two or more seated", () => {
    expect(isOwner(room(), "amir")).toBe(true);
    expect(isOwner(room(), "sara")).toBe(false);
    expect(canStart(room(), "sara")).toBe(false);
    expect(canStart(room(), "amir")).toBe(true);
    expect(canStart(room({ participants: [person("amir")] }), "amir")).toBe(false);
  });

  test("tells a seated player from a visitor", () => {
    expect(isParticipant(room(), "sara")).toBe(true);
    expect(isParticipant(room(), "stranger")).toBe(false);
  });

  test("is full at capacity, and counts its open seats", () => {
    expect(isFull(room())).toBe(false);
    expect(isFull(room({ capacity: 2 }))).toBe(true);
    expect(openSeatCount(room({ capacity: 500 }))).toBe(498);
  });

  test("never lets capacity drop below two or below the players already in", () => {
    expect(capacityStepMin(room({ participants: [person("amir")] }))).toBe(2);
    expect(capacityStepMin(room({ participants: [person("a"), person("b"), person("c")] }))).toBe(3);
  });

  test("draws three columns for a small room and four for a bigger one", () => {
    expect(rosterColumns(room({ capacity: 3 }))).toBe(3);
    expect(rosterColumns(room({ capacity: 4 }))).toBe(4);
  });

  test("leads a started live game to its screen, and a duel to play or to its result", () => {
    expect(targetRoute(room({ phase: "running" }), "amir")).toBe("/live/m1");
    const duel = room({ format: "async_duel", phase: "running", total: 10, runs: [{ player: "amir", answered: 3, finished: false, score: 0 }] });
    expect(targetRoute(duel, "amir")).toBe("/play/m1");
    expect(targetRoute({ ...duel, runs: [{ player: "amir", answered: 10, finished: true, score: 0 }] }, "amir")).toBe("/duel/m1");
  });

  test("says how long it has left in minutes, rounded up, and in seconds for the last minute", () => {
    const t = translator("en", { "lobby.closesInMinutes": "Closes in {0} min", "lobby.closesIn": "Closes in {0}s" });
    expect(expiryText(600_000, 1_000, t)).toBe("Closes in 10 min");
    expect(expiryText(60_000, 30_500, t)).toBe("Closes in 30s");
    expect(expiryText(1_000, 5_000, t)).toBe("Closes in 0s");
  });
});
