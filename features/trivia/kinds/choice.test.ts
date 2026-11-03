import { describe, expect, test } from "bun:test";
import { MalformedAnswer } from "../../../server/core/kind";
import { choice } from "./choice";

const planets = { options: ["Mercury", "Venus", "Earth", "Mars"], correctIndex: 2 };
const play = (answer: unknown, payload = planets) => {
  const resolved = choice.resolve(payload, Math.random);
  return choice.grade(payload, resolved, choice.normalize(payload, resolved, answer));
};

describe("choice", () => {
  test("the card shows the options and does not depend on which one is correct", () => {
    const other = { ...planets, correctIndex: 0 };

    expect(choice.card(planets, choice.resolve(planets, Math.random))).toEqual({ options: ["Mercury", "Venus", "Earth", "Mars"] });
    expect(choice.card(other, choice.resolve(other, Math.random))).toEqual(choice.card(planets, choice.resolve(planets, Math.random)));
  });

  test("the reveal names the correct option", () => {
    expect(choice.reveal(planets, choice.resolve(planets, Math.random))).toEqual({ correctIndex: 2, correctOption: "Earth" });
  });

  test("only the correct option earns credit", () => {
    expect(play({ index: 2 })).toBe(1);
    expect(play({ index: 0 })).toBe(0);
  });

  test("refuses answers no client could send", () => {
    for (const answer of [undefined, null, {}, { index: "2" }, { index: 1.5 }, { index: -1 }, { index: 4 }]) {
      expect(() => play(answer)).toThrow(MalformedAnswer);
    }
  });
});

describe("authoring a choice question", () => {
  test("keeps four distinct options and the correct index, and nothing else", () => {
    expect(choice.check({ ...planets, target: { shape: "country", countryCode: "NL" } })).toEqual({ ok: true, payload: planets });
  });

  test("refuses the wrong number of options, a blank or repeated one, and an index out of range", () => {
    expect(choice.check({ options: ["A", "B", "C"], correctIndex: 0 })).toEqual({ ok: false, error: "bad_choices" });
    expect(choice.check({ options: ["A", "B", " ", "D"], correctIndex: 0 })).toEqual({ ok: false, error: "bad_choices" });
    expect(choice.check({ options: ["A", "B", "A", "D"], correctIndex: 0 })).toEqual({ ok: false, error: "bad_choices" });
    expect(choice.check({ options: ["A", "B", "C", "D"], correctIndex: 4 })).toEqual({ ok: false, error: "bad_correct_index" });
  });
});

describe("importing a choice question", () => {
  test("reads today's columns", () => {
    expect(choice.fromRow({ choice1: "A", choice2: "B", choice3: "C", choice4: "D", correctindex: "2" })).toEqual({ options: ["A", "B", "C", "D"], correctIndex: 2 });
  });
});
