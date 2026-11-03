import { describe, expect, test } from "bun:test";
import { poll, pollResults, type PollEvent, type PollState } from "./poll";

const T0 = 1_000_000;
const IDLE = 48 * 3600_000;
/** Three questions of four options; the last option of each is "not applicable". */
const slots = [{ options: 4, notApplicable: 3 }, { options: 4, notApplicable: 3 }, { options: 4, notApplicable: 3 }];

function run(players: string[], events: PollEvent[], questions = slots) {
  let state: PollState = poll.start({ players, slots: questions }, T0);
  const accepted: boolean[] = [];
  for (const event of events) {
    const step = poll.next(state, event);
    accepted.push(step.state !== state);
    state = step.state;
  }
  return { state, accepted };
}
const vote = (player: string, slot: number, option: number, at = T0 + 1_000): PollEvent => ({ type: "answer", player, slot, option, at });
const everyone = (players: string[], slot: number, option: number) => players.map((p) => vote(p, slot, option));

describe("poll", () => {
  test("a question stays open until every active player has answered, then the next opens", () => {
    const { state } = run(["ann", "bob"], [vote("ann", 0, 1), vote("bob", 0, 2, T0 + 5_000)]);

    expect(state).toMatchObject({ current: 1, ended: null });
    expect(state.slots[1]!.servedAt).toBe(T0 + 5_000);
  });

  test("a player may change their answer while the question is open", () => {
    const { state } = run(["ann", "bob", "cy"], [vote("ann", 0, 1), vote("ann", 0, 2)]);

    expect(state.slots[0]!.answers).toEqual({ ann: 2 });
  });

  test("ends resolved after the last question", () => {
    const players = ["ann", "bob"];
    const { state } = run(players, [0, 1, 2].flatMap((slot) => everyone(players, slot, 0)));

    expect(state.ended).toEqual({ reason: "resolved" });
  });

  test("refuses answers for another question, an option that does not exist, or from someone not playing", () => {
    const { accepted } = run(["ann", "bob"], [vote("ann", 1, 0), vote("ann", 0, 4), vote("zed", 0, 0)]);

    expect(accepted).toEqual([false, false, false]);
  });

  test("leaving makes a player inactive; fewer than two active players is no contest", () => {
    const three = run(["ann", "bob", "cy"], [vote("ann", 0, 1), vote("bob", 0, 1), { type: "leave", player: "cy", at: T0 + 2_000 }]);
    expect(three.state).toMatchObject({ current: 1, inactive: ["cy"] });

    const two = run(["ann", "bob"], [{ type: "leave", player: "bob", at: T0 + 2_000 }]);
    expect(two.state.ended).toEqual({ reason: "no_contest" });
  });

  test("a player who has not answered 48 hours after a question opened becomes inactive", () => {
    const { state } = run(["ann", "bob", "cy"], [vote("ann", 0, 1), vote("bob", 0, 1), { type: "settle", at: T0 + IDLE }]);

    expect(state).toMatchObject({ inactive: ["cy"], current: 1 });
  });
});

describe("pollResults", () => {
  test("counts each option, and a question all agreed on needs two comparable answers", () => {
    const players = ["ann", "bob", "cy"];
    const { state } = run(players, [
      vote("ann", 0, 1), vote("bob", 0, 1), vote("cy", 0, 3), // agreed: "not applicable" is left out
      vote("ann", 1, 0), vote("bob", 1, 2), vote("cy", 1, 3),
      vote("ann", 2, 3), vote("bob", 2, 3), vote("cy", 2, 0), // only one comparable answer: not agreed
    ]);

    const results = pollResults(state);

    expect(results.slots.map((s) => s && { counts: s.counts, allAgreed: s.allAgreed })).toEqual([
      { counts: [0, 2, 0, 1], allAgreed: true },
      { counts: [1, 0, 1, 1], allAgreed: false },
      { counts: [1, 0, 0, 2], allAgreed: false },
    ]);
    expect(results.allAgreedCount).toBe(1);
  });

  test("gives each pair of players their agreement, leaving out not-applicable answers", () => {
    const players = ["ann", "bob"];
    const { state } = run(players, [vote("ann", 0, 1), vote("bob", 0, 1), vote("ann", 1, 0), vote("bob", 1, 2), vote("ann", 2, 3), vote("bob", 2, 0)]);

    expect(pollResults(state).pairs).toEqual([{ players: ["ann", "bob"], same: 1, different: 1, percent: 50 }]);
  });

  test("a game that ended without a contest has no overall figures", () => {
    const { state } = run(["ann", "bob"], [vote("ann", 0, 1), vote("bob", 0, 1), { type: "leave", player: "bob", at: T0 + 2_000 }]);

    expect(pollResults(state)).toMatchObject({ pairs: null, allAgreedCount: null });
    expect(pollResults(state).slots[0]).toMatchObject({ counts: [0, 2, 0, 0] });
  });
});

describe("poll, ended by an administrator", () => {
  test("ends at once and takes no more answers", () => {
    const { state: running } = run(["ann", "bob"], [vote("ann", 0, 1)]);

    const state = poll.abort(running);

    expect(state).toMatchObject({ current: null, ended: { reason: "ended_by_admin" } });
    expect(poll.next(state, vote("bob", 0, 1)).state).toBe(state);
    expect(pollResults(state).pairs).toBeNull();
  });
});
