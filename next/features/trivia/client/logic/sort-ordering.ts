/** A sorting answer being built: the served index of the item at each place. */
export type Ordering = readonly number[];

export const served = (count: number): Ordering => Array.from({ length: count }, (_, i) => i);

/** Moves the row at one place to another; the rest slide along. A destination off either end is clamped. */
export function move(ordering: Ordering, from: number, to: number): Ordering {
  if (from < 0 || from >= ordering.length) return ordering;
  const target = Math.min(Math.max(to, 0), ordering.length - 1);
  if (target === from) return ordering;
  const next = ordering.filter((_, place) => place !== from);
  next.splice(target, 0, ordering[from]!);
  return next;
}

export const moveUp = (ordering: Ordering, place: number) => (place <= 0 ? ordering : move(ordering, place, place - 1));
export const moveDown = (ordering: Ordering, place: number) => (place >= ordering.length - 1 ? ordering : move(ordering, place, place + 1));

/** The row whose centre is nearest the pointer; a tie keeps the higher row so the drag does not flicker. */
export function targetPlace(midpoints: readonly number[], pointerY: number): number {
  let best = -1;
  let bestDistance = Infinity;
  midpoints.forEach((midpoint, i) => {
    const distance = Math.abs(midpoint - pointerY);
    if (distance < bestDistance) {
      best = i;
      bestDistance = distance;
    }
  });
  return best;
}
