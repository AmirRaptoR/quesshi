import { describe, expect, test } from "bun:test";
import { position, range, rangeLevels, WHOLE, withHigh, withLow } from "./difficulty-range";

describe("a difficulty range", () => {
  test("nothing stored, or every level, is the whole ramp, which travels as an empty list", () => {
    expect(range([])).toEqual(WHOLE);
    expect(range([1, 2, 3, 4, 5])).toEqual(WHOLE);
    expect(rangeLevels(WHOLE)).toEqual([]);
  });

  test("a narrowed range is every level between its handles", () => {
    expect(rangeLevels({ low: 2, high: 4 })).toEqual([2, 3, 4]);
    expect(rangeLevels({ low: 3, high: 3 })).toEqual([3]);
  });

  test("a stored set is read end to end, unsorted, holes and out-of-range levels dropped", () => {
    expect(range([4, 1])).toEqual({ low: 1, high: 4 });
    expect(range([0, 3, 9])).toEqual({ low: 3, high: 3 });
    expect(range([0, 7])).toEqual(WHOLE);
  });

  test("a handle pushes the other rather than crossing it", () => {
    expect(withLow({ low: 2, high: 3 }, 5)).toEqual({ low: 5, high: 5 });
    expect(withHigh({ low: 3, high: 4 }, 1)).toEqual({ low: 1, high: 1 });
    expect(withLow(WHOLE, 2)).toEqual({ low: 2, high: 5 });
  });

  test("a handle told something off the track lands on the nearest level", () => {
    expect(withLow(WHOLE, -3).low).toBe(1);
    expect(withHigh(WHOLE, 99).high).toBe(5);
  });

  test("a handle position runs the full width of the track", () => {
    expect([position(1), position(3), position(5)]).toEqual([0, 50, 100]);
  });

  test("a range survives being written out and read back", () => {
    for (const r of [{ low: 1, high: 3 }, { low: 2, high: 4 }, { low: 3, high: 3 }, WHOLE]) expect(range(rangeLevels(r))).toEqual(r);
  });
});
