import { describe, expect, test } from "bun:test";
import { addChoice, choicesOf, offeredIn, removeChoice } from "./choices";

describe("a choice poll's options in the admin's editor", () => {
  test("a new question starts with two empty choices, an existing one with its own", () => {
    expect(choicesOf(undefined)).toEqual(["", ""]);
    expect(choicesOf({})).toEqual(["", ""]);
    expect(choicesOf({ options: ["Tea", "Coffee", "Water"] })).toEqual(["Tea", "Coffee", "Water"]);
  });

  test("a stored set outside two to eight starts over rather than being shown half valid", () => {
    expect(choicesOf({ options: ["Tea"] })).toEqual(["", ""]);
    expect(choicesOf({ options: Array(9).fill("x") })).toEqual(["", ""]);
  });

  test("choices can be added up to eight and removed down to two", () => {
    const eight = Array.from({ length: 8 }, (_, i) => `c${i}`);
    expect(addChoice(["a", "b"])).toEqual(["a", "b", ""]);
    expect(addChoice(eight)).toEqual(eight);
    expect(removeChoice(["a", "b", "c"], 1)).toEqual(["a", "c"]);
    expect(removeChoice(["a", "b"], 0)).toEqual(["a", "b"]);
  });
});

describe("the voting setup's categories", () => {
  const categories = [{ id: "fun", langs: ["en", "fa"] }, { id: "music", langs: ["en"] }];

  test("only categories with questions in the chosen language are offered", () => {
    expect(offeredIn(categories, "fa").map((c) => c.id)).toEqual(["fun"]);
    expect(offeredIn(categories, "en").map((c) => c.id)).toEqual(["fun", "music"]);
  });
});
