import type { Kind } from "../../server/core/kind";
import type { Media } from "../../server/core/questions.store";
import { choice } from "./kinds/choice";
import { mapPick } from "./kinds/map-pick";
import { order } from "./kinds/order";

/** The trivia kinds, by the id stored with each question. */
export const kinds: Record<TriviaItem["kind"], Kind<unknown, unknown, unknown, unknown, unknown>> = { choice, order, map_pick: mapPick };
export const TRIVIA_KINDS = Object.keys(kinds) as TriviaItem["kind"][];

/** A drawn question, as a game plays it. */
export interface TriviaItem {
  prompt: string;
  level: number | null;
  explanation: string | null;
  kind: "choice" | "order" | "map_pick";
  payload: unknown;
  media: Media | null;
}
