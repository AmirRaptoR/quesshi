/** Input no honest client could have produced. The answer is refused, never graded as wrong. */
export class MalformedAnswer extends Error {}

/**
 * The shape of one question. A kind is pure: no database, network or clock.
 * Payload is what an author wrote; Resolved is decided once when a session draws the question.
 */
export interface Kind<Payload, Resolved, Card, Stored, Reveal> extends KindRules<Payload> {
  id: string;
  resolve(payload: Payload, random: () => number): Resolved;
  /** What players see while answering. Must not depend on the correct answer. */
  card(payload: Payload, resolved: Resolved): Card;
  /** The answer side, shown after the question closes. */
  reveal(payload: Payload, resolved: Resolved): Reveal;
  /** Turns a submitted answer into its stored form, or throws MalformedAnswer. */
  normalize(payload: Payload, resolved: Resolved, answer: unknown): Stored;
  /** Credit from 0 to 1. */
  grade(payload: Payload, resolved: Resolved, stored: Stored): number;
}

/** An authored payload cleaned to the kind's own fields, or today's error code for the first broken rule. */
export type Checked<Payload> = { ok: true; payload: Payload } | { ok: false; error: string };

/** What the admin needs to author questions of a kind. */
export interface KindRules<Payload = unknown> {
  /** Whether questions of this kind carry a level from 1 to 5. */
  levelled: boolean;
  check(payload: unknown): Checked<Payload>;
}

/** Trimmed non-blank strings, distinct ignoring case, as today's choice rules; null when not. */
export function distinctTexts(value: unknown): string[] | null {
  if (!Array.isArray(value) || !value.every((v) => typeof v === "string" && v.trim())) return null;
  const texts = (value as string[]).map((v) => v.trim());
  return new Set(texts.map((t) => t.toLowerCase())).size === texts.length ? texts : null;
}

export const refuse = (error: string) => ({ ok: false as const, error });
