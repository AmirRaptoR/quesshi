import { describe, expect, test } from "bun:test";
import { pollKinds } from "./options";

describe("authoring a poll question", () => {
  test("a choice poll keeps two to eight distinct options", () => {
    expect(pollKinds.poll_choice.check({ options: ["Tea", "Coffee"], correctIndex: 0 })).toEqual({ ok: true, payload: { options: ["Tea", "Coffee"] } });
    expect(pollKinds.poll_choice.check({ options: ["Tea"] })).toEqual({ ok: false, error: "too_few_choices" });
    expect(pollKinds.poll_choice.check({ options: Array.from({ length: 9 }, (_, i) => `O${i}`) })).toEqual({ ok: false, error: "too_many_choices" });
    expect(pollKinds.poll_choice.check({ options: ["Tea", " tea "] })).toEqual({ ok: false, error: "bad_choices" });
  });

  test("a roster poll has no options of its own", () => {
    expect(pollKinds.poll_roster.check({})).toEqual({ ok: true, payload: {} });
    expect(pollKinds.poll_roster.check({ options: ["Ann"] })).toEqual({ ok: false, error: "players_has_choices" });
  });
});

describe("importing a poll question", () => {
  test("a choice poll reads up to eight choice columns, skipping empty ones", () => {
    expect(pollKinds.poll_choice.fromRow({ choice1: "Tea", choice3: "Coffee" })).toEqual({ options: ["Tea", "Coffee"] });
  });

  test("a roster poll reads no columns", () => {
    expect(pollKinds.poll_roster.fromRow({ choice1: "Ann" })).toEqual({ options: ["Ann"] });
  });
});
