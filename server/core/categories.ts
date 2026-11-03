/** Today's category id rule: lowercase, spaces to hyphens, ASCII letters, digits, hyphens and underscores only. */
export function slugify(value: string): string {
  const kept = value.trim().toLowerCase().replace(/\s/g, "-").replace(/[^a-z0-9_-]/g, "");
  return kept.replace(/-{2,}/g, "-").replace(/^-+|-+$/g, "");
}

const ONE_EMOJI = /^\p{RGI_Emoji}$/v;

/** The trimmed icon when it is exactly one emoji, else null. */
export function categoryIcon(value: string): string | null {
  const icon = value.trim();
  return ONE_EMOJI.test(icon) ? icon : null;
}

/** Today's limit on categories in one game. */
const CATEGORIES_PER_GAME = 3;

/** Today's category choice: the eligible picks, or eligible categories at random when there are none. */
export function selectCategories(eligible: readonly string[], picks: readonly string[], random: () => number): string[] {
  if (picks.length) return [...new Set(picks)].filter((id) => eligible.includes(id)).slice(0, CATEGORIES_PER_GAME);
  const pool = [...eligible];
  for (let i = pool.length - 1; i > 0; i--) {
    const j = Math.floor(random() * (i + 1));
    [pool[i], pool[j]] = [pool[j]!, pool[i]!];
  }
  return pool.slice(0, CATEGORIES_PER_GAME);
}
