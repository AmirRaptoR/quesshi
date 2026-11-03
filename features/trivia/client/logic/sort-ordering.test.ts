import { describe, expect, test } from "bun:test";
import { move, moveDown, moveUp, served, targetPlace } from "./sort-ordering";

const items = ["Everest", "K2", "Kangchenjunga", "Lhotse"];
const arrangement = (ordering: readonly number[]) => ordering.map((i) => items[i]).join(" | ");

describe("a sorting answer", () => {
  test("starts as the order the card served", () => {
    expect(arrangement(served(4))).toBe("Everest | K2 | Kangchenjunga | Lhotse");
  });

  test("a drag takes the row out and slides the rest along", () => {
    expect(arrangement(move(served(4), 0, 3))).toBe("K2 | Kangchenjunga | Lhotse | Everest");
  });

  test("a sequence of drags composes", () => {
    expect(arrangement(move(move(served(4), 0, 3), 3, 1))).toBe("K2 | Everest | Kangchenjunga | Lhotse");
  });

  test("a move to where the row already is, or from no row, changes nothing", () => {
    const ordering = served(4);
    expect(move(ordering, 2, 2)).toBe(ordering);
    expect(move(ordering, 9, 0)).toBe(ordering);
  });

  test("a destination past either end is clamped rather than rejected", () => {
    expect(move(served(4), 0, 99)).toEqual([1, 2, 3, 0]);
    expect(move(served(4), 3, -99)).toEqual([3, 0, 1, 2]);
  });

  test("the arrow keys move a row one place, and arrowing off an end is a no-op", () => {
    expect(arrangement(moveUp(moveDown(served(4), 0), 3))).toBe("K2 | Everest | Lhotse | Kangchenjunga");
    const ordering = served(4);
    expect(moveUp(ordering, 0)).toBe(ordering);
    expect(moveDown(ordering, 3)).toBe(ordering);
  });
});

describe("a dragged pointer", () => {
  const rows = [100, 156, 212, 268];

  test("targets the nearest row, keeping the higher one on a tie, and the end rows past the ends", () => {
    expect(targetPlace(rows, 156)).toBe(1);
    expect(targetPlace(rows, 140)).toBe(1);
    expect(targetPlace(rows, 128)).toBe(0);
    expect(targetPlace(rows, -900)).toBe(0);
    expect(targetPlace(rows, 9000)).toBe(3);
  });

  test("has nothing to drop onto in an empty list", () => {
    expect(targetPlace([], 100)).toBe(-1);
  });
});
