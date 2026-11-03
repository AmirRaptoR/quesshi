/** Input no honest client could have produced. The answer is refused, never graded as wrong. */
export class MalformedAnswer extends Error {}

/**
 * The shape of one question. A kind is pure: no database, network or clock.
 * Payload is what an author wrote; Resolved is decided once when a session draws the question.
 */
export interface Kind<Payload, Resolved, Card, Stored, Reveal> {
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
