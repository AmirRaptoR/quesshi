/** Today's twelve avatars; PUT /api/me accepts only these. */
export const AVATAR_SEEDS: readonly string[] = Array.from({ length: 12 }, (_, i) => `avatar-${i}`);

export const isAvatarSeed = (seed: string): boolean => AVATAR_SEEDS.includes(seed);

/**
 * Today's avatar colour: a hue from the seed, stable across reloads (today's per-process hash was not).
 * The twelve swatches are spread around the colour wheel so no two look alike.
 */
export function tint(seed: string): string {
  const swatch = AVATAR_SEEDS.indexOf(seed);
  if (swatch >= 0) return `hsl(${swatch * 30} 55% 62%)`;
  let hash = 0x811c9dc5;
  for (const char of seed) hash = Math.imul(hash ^ char.codePointAt(0)!, 0x01000193);
  return `hsl(${(hash >>> 0) % 360} 55% 62%)`;
}

export const initial = (name: string): string => ([...name][0] ?? "?").toUpperCase();
