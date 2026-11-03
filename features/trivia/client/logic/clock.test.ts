import { describe, expect, test } from "bun:test";
import { remainingSeconds, shouldTick } from "./clock";

describe("the countdown", () => {
  test("rounds a part second up and never goes below zero", () => {
    expect(remainingSeconds(10_000, 0)).toBe(10);
    expect(remainingSeconds(10_000, 500)).toBe(10);
    expect(remainingSeconds(10_000, 9_001)).toBe(1);
    expect(remainingSeconds(10_000, 10_000)).toBe(0);
    expect(remainingSeconds(10_000, 20_000)).toBe(0);
  });

  test("ticks once a second over the last three seconds only", () => {
    expect(shouldTick(3, null)).toBe(true);
    expect(shouldTick(3, 3)).toBe(false);
    expect(shouldTick(2, 3)).toBe(true);
    expect(shouldTick(4, null)).toBe(false);
    expect(shouldTick(0, 1)).toBe(false);
  });
});
