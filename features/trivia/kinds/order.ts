import { cells, distinctTexts, MalformedAnswer, refuse, type Kind } from "../../../server/core/kind";

export interface OrderItem { id: string; text: string }
/** The items in their correct order. */
export interface OrderPayload { items: OrderItem[] }
export interface OrderResolved { displayIds: string[] }
export interface OrderCard { items: OrderItem[] }
export interface OrderAnswer { itemIds: string[] }
export interface OrderReveal { itemIds: string[] }

/** Put four items in the right order. */
export const order: Kind<OrderPayload, OrderResolved, OrderCard, OrderAnswer, OrderReveal> = {
  id: "order",
  levelled: true,
  columns: ["item1", "item2", "item3", "item4"],
  fromRow: (row) => ({ items: cells(row, ["item1", "item2", "item3", "item4"]).map((text, i) => ({ id: String(i + 1), text })) }),
  example: { prompt: "Order these cities by population, largest first", item1: "Tokyo", item2: "Delhi", item3: "Cairo", item4: "Lima" },
  check(payload) {
    const items = ((payload ?? {}) as Record<string, unknown>).items;
    if (!Array.isArray(items) || items.length !== 4) return refuse("bad_choices");
    const ids = items.map((item) => (item as Partial<OrderItem> | null)?.id);
    const texts = distinctTexts(items.map((item) => (item as Partial<OrderItem> | null)?.text));
    if (!texts || !ids.every((id) => typeof id === "string" && id) || new Set(ids).size !== 4) return refuse("bad_choices");
    return { ok: true, payload: { items: texts.map((text, i) => ({ id: ids[i] as string, text })) } };
  },
  // Shuffle the ids sorted by id, never the stored (correct) order, so the arrangement gives nothing away.
  resolve(payload, random) {
    const ids = payload.items.map((item) => item.id).sort();
    for (let i = ids.length - 1; i > 0; i--) {
      const j = Math.floor(random() * (i + 1));
      [ids[i], ids[j]] = [ids[j]!, ids[i]!];
    }
    return { displayIds: ids };
  },
  card(payload, resolved) {
    const byId = new Map(payload.items.map((item) => [item.id, item]));
    return { items: resolved.displayIds.map((id) => byId.get(id)!) };
  },
  reveal: (payload) => ({ itemIds: payload.items.map((item) => item.id) }),
  normalize(payload, _resolved, answer) {
    const itemIds = (answer as Partial<OrderAnswer> | null)?.itemIds;
    const known = new Set(payload.items.map((item) => item.id));
    const valid = Array.isArray(itemIds)
      && itemIds.length === known.size
      && new Set(itemIds).size === itemIds.length
      && itemIds.every((id) => typeof id === "string" && known.has(id));
    if (!valid) throw new MalformedAnswer("An order answer must place each item exactly once.");
    return { itemIds };
  },
  grade: (payload, _resolved, stored) => (stored.itemIds.every((id, i) => id === payload.items[i]!.id) ? 1 : 0),
};
