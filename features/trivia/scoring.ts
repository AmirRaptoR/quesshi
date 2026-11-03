const BASE = 100;
const MAX_SPEED_BONUS = 60;
/** How late an answer may arrive after the time limit and still count, for slow networks. */
export const NETWORK_GRACE_MS = 2_000;
const LEVEL_WEIGHT: Record<number, number> = { 1: 0.5, 2: 0.8, 4: 1.3, 5: 1.6 };

/** Points for an answer: 100 plus a speed bonus, weighed by the question's level and the credit earned. */
export function score({ credit, takenMs, limitMs, level }: { credit: number; takenMs: number; limitMs: number; level: number | null }): number {
  if (takenMs > limitMs + NETWORK_GRACE_MS) return 0;
  const elapsed = Math.min(Math.max(takenMs, 0), limitMs);
  const raw = BASE + MAX_SPEED_BONUS * (1 - elapsed / limitMs);
  return roundHalfToEven(raw * (LEVEL_WEIGHT[level ?? 3] ?? 1) * credit);
}

/** Today's application rounds with .NET's default, which sends halves to the nearest even number. */
function roundHalfToEven(value: number): number {
  const floor = Math.floor(value);
  const fraction = value - floor;
  if (Math.abs(fraction - 0.5) > 1e-9) return Math.round(value);
  return floor % 2 === 0 ? floor : floor + 1;
}
