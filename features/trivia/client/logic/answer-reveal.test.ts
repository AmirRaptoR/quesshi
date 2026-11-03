import { describe, expect, test } from "bun:test";
import { sortRows, textsOf } from "./answer-reveal";

const items = [{ id: "a", text: "Everest" }, { id: "b", text: "K2" }, { id: "c", text: "Kangchenjunga" }, { id: "d", text: "Lhotse" }];
const correct = ["Everest", "K2", "Kangchenjunga", "Lhotse"];

describe("a sorting reveal", () => {
  test("a stored answer names the items it places", () => {
    expect(textsOf(items, ["c", "a", "d", "b"])).toEqual(["Kangchenjunga", "Everest", "Lhotse", "K2"]);
  });

  test("a perfect answer has every place right", () => {
    expect(sortRows(correct, correct).every((row) => row.right)).toBe(true);
  });

  test("shows the correct order beside the submitted one, place by place", () => {
    const rows = sortRows(correct, ["K2", "Everest", "Kangchenjunga", "Lhotse"]);
    expect(rows.map((r) => [r.correct, r.placed, r.right])).toEqual([
      ["Everest", "K2", false], ["K2", "Everest", false], ["Kangchenjunga", "Kangchenjunga", true], ["Lhotse", "Lhotse", true],
    ]);
  });

  test("an unanswered or unreadable answer still reveals the correct order, with nothing placed", () => {
    expect(textsOf(items, null)).toBeNull();
    expect(textsOf(items, ["a", "b"])).toBeNull();
    expect(textsOf(items, ["a", "b", "c", "x"])).toBeNull();
    const rows = sortRows(correct, null);
    expect(rows.map((r) => [r.correct, r.placed, r.right])).toEqual(correct.map((text) => [text, null, false]));
  });
});
