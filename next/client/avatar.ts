/** Today's twelve avatars; PUT /api/me accepts only these. */
export const AVATAR_SEEDS: readonly string[] = Array.from({ length: 12 }, (_, i) => `avatar-${i}`);

export const isAvatarSeed = (seed: string): boolean => AVATAR_SEEDS.includes(seed);

/** Today's avatar colour: a hue from the seed. Unlike today's per-process hash it is stable across reloads. */
export function tint(seed: string): string {
  let hash = 0;
  for (const char of seed) hash = (Math.imul(hash, 31) + char.codePointAt(0)!) | 0;
  return `hsl(${Math.abs(hash) % 360} 55% 62%)`;
}

export const initial = (name: string): string => ([...name][0] ?? "?").toUpperCase();
