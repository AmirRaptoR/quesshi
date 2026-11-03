/** A contiguous run of difficulty levels, what the two-handle slider picks. */
export interface DifficultyRange { low: number; high: number }

export const EASIEST = 1;
export const HARDEST = 5;
export const WHOLE: DifficultyRange = { low: EASIEST, high: HARDEST };

const clamp = (level: number) => Math.min(Math.max(level, EASIEST), HARDEST);
export const isWhole = (r: DifficultyRange) => r.low <= EASIEST && r.high >= HARDEST;

/** A stored level list as a range: empty is the whole ramp, anything else read end to end. */
export function range(levels: readonly number[]): DifficultyRange {
  const picked = levels.filter((level) => level >= EASIEST && level <= HARDEST);
  return picked.length === 0 ? WHOLE : { low: Math.min(...picked), high: Math.max(...picked) };
}

/** The list to send; the whole ramp goes as an empty list, which means the game's own ramp. */
export const rangeLevels = (r: DifficultyRange): number[] => (isWhole(r) ? [] : Array.from({ length: r.high - r.low + 1 }, (_, i) => r.low + i));

/** The easy handle moved; it pushes the hard one rather than being refused, so the inputs never disagree with the model. */
export function withLow(r: DifficultyRange, value: number): DifficultyRange {
  const low = clamp(value);
  return { low, high: Math.max(r.high, low) };
}

export function withHigh(r: DifficultyRange, value: number): DifficultyRange {
  const high = clamp(value);
  return { low: Math.min(r.low, high), high };
}

/** Where a handle sits along the track, 0 to 100. */
export const position = (level: number) => ((clamp(level) - EASIEST) * 100) / (HARDEST - EASIEST);
