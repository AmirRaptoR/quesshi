import { describe, expect, test } from "bun:test";
import { asyncDuel, type DuelEvent, type DuelState } from "./async-duel";

const T0 = 1_000_000;
const LIMIT = 20_000, GRACE = 2_000, FORFEIT = 48 * 3600_000;

function duel(players = ["ann", "bob"], questions = 2, events: DuelEvent[] = []) {
  let state: DuelState = asyncDuel.start({ players, levels: Array(questions).fill(null), createdAt: T0 });
  const results: ReturnType<typeof asyncDuel.next>["result"][] = [];
  for (const event of events) {
    const step = asyncDuel.next(state, event);
    state = step.state;
    results.push(step.result);
  }
  return { state, results, last: () => results.at(-1) };
}

const serve = (player: string, at: number): DuelEvent => ({ type: "serve", player, at });
const answer = (player: string, slot: number, at: number, credit = 1): DuelEvent => ({ type: "answer", player, slot, credit, at });

describe("async duel", () => {
  test("each player is served their own next question and starts its clock", () => {
    const { results } = duel(["ann", "bob"], 2, [serve("ann", T0 + 1_000), serve("bob", T0 + 5_000)]);

    expect(results).toEqual([{ type: "served", slot: 0, servedAt: T0 + 1_000 }, { type: "served", slot: 0, servedAt: T0 + 5_000 }]);
  });

  test("asking again for an open question returns it without restarting the clock", () => {
    const { last } = duel(["ann", "bob"], 2, [serve("ann", T0 + 1_000), serve("ann", T0 + 9_000)]);

    expect(last()).toEqual({ type: "served", slot: 0, servedAt: T0 + 1_000 });
  });

  test("an answer is scored by how fast it came, and the next question follows", () => {
    const { results } = duel(["ann", "bob"], 2, [serve("ann", T0), answer("ann", 0, T0 + 10_000), serve("ann", T0 + 20_000)]);

    expect(results.slice(1)).toEqual([{ type: "answered", slot: 0, points: 130 }, { type: "served", slot: 1, servedAt: T0 + 20_000 }]);
  });

  test("an answer after the time limit and grace earns nothing", () => {
    const { last } = duel(["ann", "bob"], 2, [serve("ann", T0), answer("ann", 0, T0 + LIMIT + GRACE + 1)]);

    expect(last()).toEqual({ type: "answered", slot: 0, points: 0 });
  });

  test("asking for the next question after the open one ran out records a miss and moves on", () => {
    const { state, last } = duel(["ann", "bob"], 2, [serve("ann", T0), serve("ann", T0 + LIMIT + GRACE + 1)]);

    expect(last()).toEqual({ type: "served", slot: 1, servedAt: T0 + LIMIT + GRACE + 1 });
    expect(state.runs.ann!.answers).toEqual([{ slot: 0, points: 0, missed: true }]);
  });

  test("refuses an answer for a question that was not served or not the current one", () => {
    const { results } = duel(["ann", "bob"], 2, [answer("ann", 0, T0), serve("ann", T0), answer("ann", 1, T0 + 1_000), answer("zed", 0, T0)]);

    expect(results).toEqual([{ type: "refused" }, { type: "served", slot: 0, servedAt: T0 }, { type: "refused" }, { type: "refused" }]);
  });

  test("ends when everyone has finished, ranked by score with shared places", () => {
    const { state } = duel(["ann", "bob", "cy"], 1, [
      serve("ann", T0), answer("ann", 0, T0 + 1_000),
      serve("bob", T0), answer("bob", 0, T0 + 1_000),
      serve("cy", T0), answer("cy", 0, T0 + 1_000, 0),
    ]);

    expect(state.ended).toEqual({ reason: "resolved", standings: [
      { player: "ann", score: 157, place: 1, outcome: "draw" },
      { player: "bob", score: 157, place: 1, outcome: "draw" },
      { player: "cy", score: 0, place: 3, outcome: "loss" },
    ] });
  });

  test("is forfeited 48 hours after it was created, with standings by score so far", () => {
    const { state, last } = duel(["ann", "bob"], 2, [serve("ann", T0), answer("ann", 0, T0 + 1_000), serve("bob", T0 + FORFEIT)]);

    expect(last()).toEqual({ type: "refused" });
    expect(state.ended).toEqual({ reason: "forfeited", standings: [
      { player: "ann", score: 157, place: 1, outcome: "win" },
      { player: "bob", score: 0, place: 2, outcome: "loss" },
    ] });
  });
});
