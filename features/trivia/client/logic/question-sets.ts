import type { Lang } from "../../../../client/i18n";
import { SET_COUNTS } from "./set-prompt";

/** A player's private question set, as /api/question-sets returns it. */
export interface QuestionSetView { id: string; name: string; lang: Lang; createdAt: string; questionCount: number }
/** One question inside a set; a set holds only multiple-choice questions. */
export interface SetQuestionView { id: string; prompt: string; level: number | null; payload: { options: string[]; correctIndex: number } }
export interface ImportResult { accepted: number; rejected: { row: number; reason: string }[] }

/** The most sets a player owns, as the server enforces it. */
export const MAX_SETS = 20;

/** Only someone with an account makes sets; a guest sees no way to start one. */
export const canCreateSets = (me: { guest: boolean } | null): boolean => me !== null && !me.guest;

/** The count field's value as a whole number inside the allowed range; out-of-range or blank input snaps to the nearest bound. */
export function clampCount(input: string): number {
  const value = Number.parseInt(input, 10);
  if (Number.isNaN(value)) return SET_COUNTS.initial;
  return Math.min(SET_COUNTS.max, Math.max(SET_COUNTS.min, value));
}

/** The translation key for a rejected row's reason code; a code nobody wrote text for falls back to a generic line. */
export function reasonKey(reason: string, has: (key: string) => boolean): string {
  const key = `sets.reason.${reason}`;
  return has(key) ? key : "sets.reason.other";
}

/** Puts text on the clipboard; false when the browser refuses (no permission, or an insecure page). */
export async function copyText(text: string, clipboard: Pick<Clipboard, "writeText"> | undefined = globalThis.navigator?.clipboard): Promise<boolean> {
  if (!clipboard) return false;
  try {
    await clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}
