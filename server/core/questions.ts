import type { QuestionsStore, StoredQuestion } from "./questions.store";

export class NotEnoughQuestions extends Error {}

const ALL_LEVELS = [1, 2, 3, 4, 5];

/**
 * Today's difficulty ramp: the game climbs through the allowed levels from first to last question.
 * Picking easy and hard spends the first half easy and the second half hard.
 */
export function levelForSlot(slot: number, total: number, allowed: readonly number[]): number {
  if (total <= 1 || allowed.length === 1) return allowed[Math.floor(allowed.length / 2)]!;
  // .NET's MidpointRounding.AwayFromZero, as today; the values are never negative.
  const index = Math.floor(((allowed.length - 1) * slot) / (total - 1) + 0.5);
  return allowed[Math.min(Math.max(index, 0), allowed.length - 1)]!;
}

export interface DrawOptions {
  lang: string;
  /** Chosen categories, taken in turn; null for any category. */
  categoryIds: readonly string[] | null;
  /** Chosen levels; empty for all five; null for games whose questions have no level (polls). */
  levels: readonly number[] | null;
  count: number;
  /** The kinds the session's format can play. */
  kinds: readonly string[];
}

/** Draws a game's questions, slot by slot, as today's question set builder does. */
export function drawQuestions(store: QuestionsStore, options: DrawOptions): StoredQuestion[] {
  if (options.categoryIds?.length === 0) throw new NotEnoughQuestions("The category scope is empty.");
  const levelled = options.levels !== null;
  const allowed = options.levels?.length ? [...new Set(options.levels)].sort((a, b) => a - b) : ALL_LEVELS;
  const categories = options.categoryIds ? [...new Set(options.categoryIds)] : null;
  const picked: StoredQuestion[] = [];
  const take = (categoryIds: readonly string[] | null, level: number) =>
    store.randomApproved({ lang: options.lang, kinds: options.kinds, categoryIds, level: levelled ? level : null, exclude: picked.map((q) => q.id) });

  for (let slot = 0; slot < options.count; slot++) {
    const preferred = categories ? [categories[slot % categories.length]!] : null;
    const attempt = (level: number) => take(preferred, level) ?? (categories && categories.length > 1 ? take(categories, level) : null);
    const question = attempt(levelForSlot(slot, options.count, allowed)) ?? firstOf(allowed, attempt);
    if (!question) throw new NotEnoughQuestions(`Not enough approved ${options.lang} questions (got ${picked.length} of ${options.count}).`);
    picked.push(question);
  }
  return picked;
}

function firstOf<T>(levels: readonly number[], attempt: (level: number) => T | null): T | null {
  for (const level of levels) {
    const found = attempt(level);
    if (found) return found;
  }
  return null;
}
