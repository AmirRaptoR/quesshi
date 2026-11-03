import { describe, expect, test } from "bun:test";
import { score } from "./scoring";

const LIMIT = 20_000;

describe("score", () => {
  test("a correct answer earns 100 plus a speed bonus of up to 60", () => {
    expect(score({ credit: 1, takenMs: 0, limitMs: LIMIT, level: null })).toBe(160);
    expect(score({ credit: 1, takenMs: 10_000, limitMs: LIMIT, level: null })).toBe(130);
    expect(score({ credit: 1, takenMs: LIMIT, limitMs: LIMIT, level: null })).toBe(100);
  });

  test("an answer within the 2 s grace earns no speed bonus, and one after it earns nothing", () => {
    expect(score({ credit: 1, takenMs: LIMIT + 2_000, limitMs: LIMIT, level: null })).toBe(100);
    expect(score({ credit: 1, takenMs: LIMIT + 2_001, limitMs: LIMIT, level: null })).toBe(0);
  });

  test("a wrong answer earns nothing", () => {
    expect(score({ credit: 0, takenMs: 0, limitMs: LIMIT, level: null })).toBe(0);
  });

  test("the question's level weighs the points", () => {
    const at = (level: number | null) => score({ credit: 1, takenMs: LIMIT, limitMs: LIMIT, level });
    expect([1, 2, 3, 4, 5, null].map(at)).toEqual([50, 80, 100, 130, 160, 100]);
  });

  test("rounds halves to the nearest even number, as today's application does", () => {
    // 100 + 60 × 0.35 = 121, × 0.5 = 60.5 → 60; 100 + 60 × 0.45 = 127, × 0.5 = 63.5 → 64.
    expect(score({ credit: 1, takenMs: 13_000, limitMs: LIMIT, level: 1 })).toBe(60);
    expect(score({ credit: 1, takenMs: 11_000, limitMs: LIMIT, level: 1 })).toBe(64);
  });
});
