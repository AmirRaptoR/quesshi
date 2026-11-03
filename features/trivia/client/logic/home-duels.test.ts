import { describe, expect, test } from "bun:test";
import type { DuelRoomView } from "../types";
import { duelRoute, opponentOf, split, standing } from "./home-duels";

const person = (id: string) => ({ id, displayName: id, avatarSeed: id, guest: false });
const duel = (id: string, fields: Partial<DuelRoomView> = {}): DuelRoomView => ({
  id, format: "async_duel", code: `C${id}`, owner: "me", capacity: 2, settings: { lang: "en", questions: 10, categoryIds: [], levels: [] },
  phase: "running", endedReason: null, lobbyEndsAt: 0, participants: [person("me"), person("ann")], total: 10,
  runs: [{ player: "me", answered: 3, finished: false, score: 200 }, { player: "ann", answered: 10, finished: true, score: null }], ...fields,
});
const done = { runs: [{ player: "me", answered: 10, finished: true, score: 900 }, { player: "ann", answered: 2, finished: false, score: 100 }] };

describe("the offline tab's duels", () => {
  test("splits the ones I can play from the ones waiting on others, keeping the server's newest-first order, and drops finished duels", () => {
    const lists = split([duel("a"), duel("b", done), duel("c"), duel("d", { phase: "lobby", runs: [] }), duel("e", { phase: "ended" })], "me");

    expect(lists.yourTurn.map((d) => d.id)).toEqual(["a", "c"]);
    expect(lists.waitingOnThem.map((d) => d.id)).toEqual(["b", "d"]);
  });

  test("opens a lobby at its code, a duel with questions left at play, and anything else at its result", () => {
    expect(duelRoute(duel("d", { phase: "lobby" }), "me")).toBe("/lobby/Cd");
    expect(duelRoute(duel("a"), "me")).toBe("/play/a");
    expect(duelRoute(duel("b", done), "me")).toBe("/duel/b");
  });

  test("says what is useful under the name: waiting, my score, not started, or questions left", () => {
    expect(standing(duel("d", { phase: "lobby", participants: [person("me")] }), "me")).toEqual(["home.waiting"]);
    expect(standing(duel("b", done), "me")).toEqual(["home.youScored", 900]);
    expect(standing(duel("n", { runs: [{ player: "me", answered: 0, finished: false, score: 0 }] }), "me")).toEqual(["home.notStarted"]);
    expect(standing(duel("a"), "me")).toEqual(["home.questionsLeft", 7]);
  });

  test("names the first other player as the opponent", () => {
    expect(opponentOf(duel("a"), "me")?.id).toBe("ann");
    expect(opponentOf(duel("d", { participants: [person("me")] }), "me")).toBeNull();
  });
});
