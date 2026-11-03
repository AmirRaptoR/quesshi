import { describe, expect, test } from "bun:test";
import { live, type LiveEffect, type LiveEvent, type LiveState } from "./live";

const T0 = 1_000_000;
const COUNTDOWN = 3_000, QUESTION = 20_000, GRACE = 2_000, REVEAL = 3_000;

/** Plays events in order from the start, returning the final state and every effect along the way. */
function play(players: string[], questions: number, events: LiveEvent[] = []) {
  let { state, effects } = live.start({ players, levels: Array(questions).fill(null) }, T0);
  const all: LiveEffect[] = [...effects];
  for (const event of events) {
    ({ state, effects } = live.next(state, event));
    all.push(...effects);
  }
  return { state, effects: all };
}

const timeout = (at: number): LiveEvent => ({ type: "timeout", at });
const answer = (player: string, slot: number, at: number, credit = 1): LiveEvent => ({ type: "answer", player, slot, credit, at });
const leave = (player: string, at: number): LiveEvent => ({ type: "leave", player, at });
/** When slot n's question opens, if every earlier round ran to its schedule. */
const opens = (slot: number) => T0 + COUNTDOWN + slot * (QUESTION + REVEAL);
const of = (effects: LiveEffect[], type: LiveEffect["type"]) => effects.filter((effect) => effect.type === type);

describe("live", () => {
  test("runs one countdown, then each question and its reveal on a shared schedule", () => {
    let { state } = play(["ann", "bob"], 2);
    expect(state).toMatchObject({ phase: "countdown", dueAt: T0 + COUNTDOWN });

    ({ state } = live.next(state, timeout(T0 + COUNTDOWN)));
    expect(state).toMatchObject({ phase: "question", slot: 0, dueAt: opens(0) + QUESTION + GRACE });

    ({ state } = live.next(state, timeout(opens(0) + QUESTION + GRACE)));
    expect(state).toMatchObject({ phase: "reveal", slot: 0, dueAt: opens(0) + QUESTION + REVEAL });

    ({ state } = live.next(state, timeout(opens(0) + QUESTION + REVEAL)));
    expect(state).toMatchObject({ phase: "question", slot: 1, dueAt: opens(1) + QUESTION + GRACE });
  });

  test("a round closes as soon as everyone has answered, and its reveal still ends on schedule", () => {
    const { state, effects } = play(["ann", "bob"], 2, [timeout(opens(0)), answer("ann", 0, opens(0) + 1_000), answer("bob", 0, opens(0) + 2_000)]);

    expect(state).toMatchObject({ phase: "reveal", slot: 0, dueAt: opens(0) + QUESTION + REVEAL });
    expect(of(effects, "released")).toEqual([{ type: "released", slot: 0 }]);
  });

  test("a missing answer closes the round at the deadline plus grace and is recorded as a miss", () => {
    const { effects } = play(["ann", "bob"], 2, [timeout(opens(0)), answer("ann", 0, opens(0) + 1_000), timeout(opens(0) + QUESTION + GRACE)]);

    expect(of(effects, "missed")).toEqual([{ type: "missed", player: "bob", slot: 0 }]);
  });

  test("three consecutive misses eliminate a player, and an answer resets the count", () => {
    const rounds = (slots: number[], bobAnswers: number[]) => slots.flatMap((slot) => [
      ...(slot === 0 ? [timeout(opens(0))] : []),
      answer("ann", slot, opens(slot) + 1_000),
      answer("cy", slot, opens(slot) + 1_000),
      ...(bobAnswers.includes(slot) ? [answer("bob", slot, opens(slot) + 1_000)] : [timeout(opens(slot) + QUESTION + GRACE)]),
      timeout(opens(slot) + QUESTION + REVEAL),
    ]);

    expect(of(play(["ann", "bob", "cy"], 6, rounds([0, 1, 2], [])).effects, "eliminated")).toEqual([{ type: "eliminated", player: "bob" }]);
    expect(of(play(["ann", "bob", "cy"], 6, rounds([0, 1, 2, 3, 4], [2])).effects, "eliminated")).toEqual([]);
  });

  test("leaving before answering stops the round waiting for that player", () => {
    const { state } = play(["ann", "bob", "cy"], 2, [timeout(opens(0)), answer("ann", 0, opens(0) + 1_000), answer("bob", 0, opens(0) + 1_000), leave("cy", opens(0) + 2_000)]);

    expect(state).toMatchObject({ phase: "reveal", slot: 0 });
  });

  test("leaving after answering does not change the round", () => {
    const { state } = play(["ann", "bob", "cy"], 2, [timeout(opens(0)), answer("ann", 0, opens(0) + 1_000), leave("ann", opens(0) + 2_000), answer("bob", 0, opens(0) + 3_000)]);

    expect(state).toMatchObject({ phase: "question", slot: 0, expected: ["ann", "bob", "cy"], answered: ["ann", "bob"] });
  });

  test("the session completes when one player is left", () => {
    const { state } = play(["ann", "bob"], 3, [timeout(opens(0)), leave("bob", opens(0) + 1_000)]);

    expect(state.ended).toMatchObject({ reason: "completed" });
  });

  test("the session completes after the last reveal", () => {
    const { state } = play(["ann", "bob"], 1, [timeout(opens(0)), answer("ann", 0, opens(0) + 1_000), answer("bob", 0, opens(0) + 1_000), timeout(opens(0) + QUESTION + REVEAL)]);

    expect(state.ended).toMatchObject({ reason: "completed" });
  });

  test("the session has no contest when everyone is eliminated", () => {
    const missEverything = [0, 1, 2].flatMap((slot) => [...(slot === 0 ? [timeout(opens(0))] : []), timeout(opens(slot) + QUESTION + GRACE), timeout(opens(slot) + QUESTION + REVEAL)]);

    const { state } = play(["ann", "bob"], 5, missEverything);

    expect(state.ended).toEqual({ reason: "no_contest", standings: [
      { player: "ann", score: 0, place: 0, outcome: "none" },
      { player: "bob", score: 0, place: 0, outcome: "none" },
    ] });
  });

  test("finishers rank by score above abandoners, who rank by how late they went out", () => {
    const { state } = play(["ann", "bob", "cy", "dee"], 2, [
      timeout(opens(0)),
      leave("dee", opens(0) + 500),
      answer("ann", 0, opens(0) + 1_000),
      answer("bob", 0, opens(0) + 10_000),
      answer("cy", 0, opens(0) + 1_000, 0),
      timeout(opens(0) + QUESTION + REVEAL),
      leave("cy", opens(1) + 500),
      answer("ann", 1, opens(1) + 1_000),
      answer("bob", 1, opens(1) + 1_000),
      timeout(opens(1) + QUESTION + REVEAL),
    ]);

    expect(state.ended).toEqual({ reason: "completed", standings: [
      { player: "ann", score: 314, place: 1, outcome: "win" },
      { player: "bob", score: 287, place: 2, outcome: "loss" },
      { player: "cy", score: 0, place: 3, outcome: "loss" },
      { player: "dee", score: 0, place: 4, outcome: "loss" },
    ] });
  });

  test("equal scores share a place, the next place is skipped, and a shared first place is a draw", () => {
    const { state } = play(["ann", "bob", "cy"], 1, [
      timeout(opens(0)),
      answer("ann", 0, opens(0) + 1_000),
      answer("bob", 0, opens(0) + 1_000),
      answer("cy", 0, opens(0) + 1_000, 0),
      timeout(opens(0) + QUESTION + REVEAL),
    ]);

    expect(state.ended?.standings.map(({ player, place, outcome }) => ({ player, place, outcome }))).toEqual([
      { player: "ann", place: 1, outcome: "draw" },
      { player: "bob", place: 1, outcome: "draw" },
      { player: "cy", place: 3, outcome: "loss" },
    ]);
  });

  test("answers are scored by speed, and the score is part of the answered effect", () => {
    const { effects } = play(["ann", "bob"], 1, [timeout(opens(0)), answer("ann", 0, opens(0)), answer("bob", 0, opens(0) + QUESTION + GRACE)]);

    expect(of(effects, "answered")).toEqual([
      { type: "answered", player: "ann", slot: 0, points: 160 },
      { type: "answered", player: "bob", slot: 0, points: 100 },
    ]);
  });

  test("ignores answers that do not count", () => {
    const ignored = (events: LiveEvent[], extra: LiveEvent) => {
      const before = play(["ann", "bob", "cy"], 2, events);
      const after = live.next(before.state, extra);
      return after.effects.length === 0 && after.state === before.state;
    };
    const open = [timeout(opens(0))];

    expect(ignored([...open, answer("ann", 0, opens(0) + 1_000)], answer("ann", 0, opens(0) + 2_000))).toBe(true); // a second answer
    expect(ignored(open, answer("ann", 1, opens(0) + 1_000))).toBe(true); // the wrong question
    expect(ignored(open, answer("zed", 0, opens(0) + 1_000))).toBe(true); // not playing
    expect(ignored(open, answer("ann", 0, opens(0) + QUESTION + GRACE + 1))).toBe(true); // after the deadline
    expect(ignored([...open, leave("cy", opens(0) + 500)], answer("cy", 0, opens(0) + 1_000))).toBe(true); // left
    expect(ignored([], answer("ann", 0, T0 + 1_000))).toBe(true); // during the countdown
  });
});

describe("live, ended by an administrator", () => {
  test("ends at once as a no contest: no scores, no places", () => {
    const { state: running } = play(["ann", "bob"], 2, [timeout(opens(0)), answer("ann", 0, opens(0) + 1_000)]);

    const { state, effects } = live.abort(running);

    expect(state.ended).toEqual({ reason: "ended_by_admin", standings: [
      { player: "ann", score: 0, place: 0, outcome: "none" }, { player: "bob", score: 0, place: 0, outcome: "none" },
    ] });
    expect(of(effects, "ended")).toHaveLength(1);
    expect(live.abort(state).effects).toEqual([]);
  });
});
