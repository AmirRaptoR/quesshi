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
