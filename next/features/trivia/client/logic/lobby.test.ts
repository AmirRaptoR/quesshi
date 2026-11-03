import { describe, expect, test } from "bun:test";
import type { DuelRoomView } from "../types";
import { segment, targetRoute } from "./lobby";

const room = (fields: Partial<DuelRoomView> = {}): DuelRoomView => ({
  id: "m1", format: "live", code: "ABCDEF", owner: "amir", capacity: 4, settings: { lang: "en", questions: 10, categoryIds: [], levels: [] },
  phase: "running", endedReason: null, lobbyEndsAt: 0, participants: [], ...fields,
});

describe("a started trivia lobby", () => {
  test("leads a live game to its screen, and a duel to play or to its result", () => {
    expect(targetRoute(room(), "amir")).toBe("/live/m1");
    const duel = room({ format: "async_duel", total: 10, runs: [{ player: "amir", answered: 3, finished: false, score: 0 }] });
    expect(targetRoute(duel, "amir")).toBe("/play/m1");
    expect(targetRoute({ ...duel, runs: [{ player: "amir", answered: 10, finished: true, score: 0 }] }, "amir")).toBe("/duel/m1");
    expect(targetRoute({ ...duel, phase: "ended" }, "amir")).toBe("/duel/m1");
  });

  test("names each format's routes", () => {
    expect([segment("live"), segment("async_duel")]).toEqual(["live", "duel"]);
  });
});
