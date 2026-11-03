import { describe, expect, test } from "bun:test";
import { chipClass, headlineKey, outcomeKey } from "./outcome";

const standings = [{ player: "me", score: 300, place: 1, outcome: "win" as const }, { player: "ann", score: 100, place: 2, outcome: "loss" as const }];
const seats = (mine: string, theirs: string) => [{ player: "me", status: mine }, { player: "ann", status: theirs }];

describe("a live game's headline", () => {
  test("is my own outcome when the game was played out", () => {
    expect(headlineKey("completed", standings, seats("done", "done"), "me")).toBe("result.win");
    expect(headlineKey("completed", standings, seats("done", "done"), "ann")).toBe("result.loss");
    expect(headlineKey("completed", standings.map((s) => ({ ...s, outcome: "draw" as const })), seats("done", "done"), "me")).toBe("result.draw");
  });

  test("says who walked away when someone left", () => {
    expect(headlineKey("completed", standings, seats("done", "left"), "me")).toBe("live.end.abandonedByThem");
    expect(headlineKey("completed", standings, seats("left", "done"), "me")).toBe("live.end.abandonedByYou");
  });

  test("is a no contest when nobody played it out or an administrator ended it", () => {
    expect(headlineKey("no_contest", standings, seats("left", "left"), "me")).toBe("live.end.noContest");
    expect(headlineKey("ended_by_admin", standings, seats("done", "done"), "me")).toBe("live.end.noContest");
  });
});

describe("a standings row", () => {
  test("names and colours each outcome, anything unknown as a loss", () => {
    expect([outcomeKey("win"), outcomeKey("draw"), outcomeKey("loss"), outcomeKey("none")]).toEqual(["result.outcome.win", "result.outcome.draw", "result.outcome.loss", "result.outcome.loss"]);
    expect([chipClass("win"), chipClass("draw"), chipClass("none")]).toEqual(["chip--win", "chip--draw", "chip--loss"]);
  });
});
