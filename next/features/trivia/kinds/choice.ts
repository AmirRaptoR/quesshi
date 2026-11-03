import { MalformedAnswer, type Kind } from "../../../server/core/kind";

export interface ChoicePayload { options: string[]; correctIndex: number }
export interface ChoiceCard { options: string[] }
export interface ChoiceAnswer { index: number }
export interface ChoiceReveal { correctIndex: number; correctOption: string }

/** One correct option among four, shown in the order they were written. */
export const choice: Kind<ChoicePayload, null, ChoiceCard, ChoiceAnswer, ChoiceReveal> = {
  id: "choice",
  resolve: () => null,
  card: (payload) => ({ options: payload.options }),
  reveal: (payload) => ({ correctIndex: payload.correctIndex, correctOption: payload.options[payload.correctIndex]! }),
  normalize(payload, _resolved, answer) {
    const index = (answer as Partial<ChoiceAnswer> | null)?.index;
    if (!Number.isInteger(index) || index! < 0 || index! >= payload.options.length) {
      throw new MalformedAnswer("A choice answer must be the index of one of the options.");
    }
    return { index: index! };
  },
  grade: (payload, _resolved, stored) => (stored.index === payload.correctIndex ? 1 : 0),
};
