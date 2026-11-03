import { MalformedAnswer, type Kind } from "../../../server/core/kind";

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
