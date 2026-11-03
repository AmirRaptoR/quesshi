/** Every player gets a stable glaze colour from their avatar seed. */
export function tint(seed: string): string {
  let hash = 0;
  for (const char of seed) hash = (hash * 31 + char.charCodeAt(0)) | 0;
  return `hsl(${Math.abs(hash) % 360} 55% 62%)`;
}

export const initial = (name: string) => (name ? name.slice(0, 1).toUpperCase() : "?");
