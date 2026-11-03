export interface SortRow { place: number; correct: string; placed: string | null; right: boolean }

/** The texts an order answer places, or null when it is missing or does not place every item. */
export function textsOf(items: readonly { id: string; text: string }[], itemIds: readonly string[] | null | undefined): string[] | null {
  if (!itemIds || itemIds.length !== items.length) return null;
  const texts = itemIds.map((id) => items.find((item) => item.id === id)?.text);
  return texts.every((text): text is string => text !== undefined) ? texts : null;
}

/** The correct order beside an answer, place by place; a partial order is no order. */
export function sortRows(correct: readonly string[], placed: readonly string[] | null): SortRow[] {
  const answer = placed && placed.length === correct.length ? placed : null;
  return correct.map((text, place) => ({ place, correct: text, placed: answer?.[place] ?? null, right: answer?.[place] === text }));
}
