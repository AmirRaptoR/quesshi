import { cells, distinctTexts, numberCell, MalformedAnswer, refuse, type Kind } from "../../../server/core/kind";

export interface ChoicePayload { options: string[]; correctIndex: number }
export interface ChoiceCard { options: string[] }
export interface ChoiceAnswer { index: number }
export interface ChoiceReveal { correctIndex: number; correctOption: string }

/** One correct option among four, shown in the order they were written. */
export const choice: Kind<ChoicePayload, null, ChoiceCard, ChoiceAnswer, ChoiceReveal> = {
  id: "choice",
  levelled: true,
  columns: ["choice1", "choice2", "choice3", "choice4", "correctindex"],
  fromRow: (row) => ({ options: cells(row, ["choice1", "choice2", "choice3", "choice4"]), correctIndex: numberCell(row.correctindex) }),
  example: { prompt: "Which sea has no coastline?", choice1: "Sargasso Sea", choice2: "Baltic Sea", choice3: "Red Sea", choice4: "Black Sea", correctindex: "0" },
  check(payload) {
    const { options, correctIndex } = (payload ?? {}) as Record<string, unknown>;
    const texts = distinctTexts(options);
    if (!texts || texts.length !== 4) return refuse("bad_choices");
    if (!Number.isInteger(correctIndex) || (correctIndex as number) < 0 || (correctIndex as number) >= 4) return refuse("bad_correct_index");
    return { ok: true, payload: { options: texts, correctIndex: correctIndex as number } };
  },
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
