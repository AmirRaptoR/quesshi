import { describe, expect, test } from "bun:test";
import { MalformedAnswer } from "../../../server/core/kind";
import { order } from "./order";

const items = [
  { id: "a", text: "Bronze Age" },
  { id: "b", text: "Iron Age" },
  { id: "c", text: "Middle Ages" },
  { id: "d", text: "Renaissance" },
];
const ages = { items };

/** A repeatable random source, so two calls arrange the same items the same way. */
const seeded = () => {
  let state = 7;
  return () => (state = (state * 16807) % 2147483647) / 2147483647;
};

describe("order", () => {
  test("the card shows every item once, arranged independently of the correct order", () => {
    const reversed = { items: [...items].reverse() };

    const card = order.card(ages, order.resolve(ages, seeded()));

    expect(card.items.map((item) => item.id).sort()).toEqual(["a", "b", "c", "d"]);
    expect(order.card(reversed, order.resolve(reversed, seeded()))).toEqual(card);
  });

  test("the reveal is the correct order", () => {
    expect(order.reveal(ages, order.resolve(ages, seeded()))).toEqual({ itemIds: ["a", "b", "c", "d"] });
  });

  test("only the exact correct order earns credit", () => {
    const resolved = order.resolve(ages, seeded());
    const grade = (itemIds: string[]) => order.grade(ages, resolved, order.normalize(ages, resolved, { itemIds }));

    expect(grade(["a", "b", "c", "d"])).toBe(1);
    expect(grade(["a", "b", "d", "c"])).toBe(0);
  });

  test("refuses answers that do not place each item exactly once", () => {
    const resolved = order.resolve(ages, seeded());
    for (const answer of [undefined, {}, { itemIds: "abcd" }, { itemIds: ["a", "b", "c"] }, { itemIds: ["a", "b", "c", "c"] }, { itemIds: ["a", "b", "c", "x"] }, { itemIds: ["a", "b", "c", 4] }]) {
      expect(() => order.normalize(ages, resolved, answer)).toThrow(MalformedAnswer);
    }
  });
});

describe("authoring an order question", () => {
  const items = [{ id: "a", text: "First" }, { id: "b", text: "Second" }, { id: "c", text: "Third" }, { id: "d", text: "Fourth" }];

  test("keeps four items in their correct order", () => {
    expect(order.check({ items, correctIndex: 0 })).toEqual({ ok: true, payload: { items } });
  });

  test("refuses the wrong number of items, a blank or repeated text, and a repeated id", () => {
    expect(order.check({ items: items.slice(0, 3) })).toEqual({ ok: false, error: "bad_choices" });
    expect(order.check({ items: [...items.slice(0, 3), { id: "d", text: "" }] })).toEqual({ ok: false, error: "bad_choices" });
    expect(order.check({ items: [...items.slice(0, 3), { id: "d", text: "First" }] })).toEqual({ ok: false, error: "bad_choices" });
    expect(order.check({ items: [...items.slice(0, 3), { id: "a", text: "Fourth" }] })).toEqual({ ok: false, error: "bad_choices" });
  });
});
