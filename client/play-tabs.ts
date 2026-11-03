/** Where the play hub's chosen tab is remembered, as today. */
export const PLAY_TAB_KEY = "quesshi.homeTab";

/** The remembered tab when an enabled feature still offers it, otherwise the first one. */
export function pickTab(stored: string | null, ids: readonly string[]): string | null {
  return stored !== null && ids.includes(stored) ? stored : ids[0] ?? null;
}
