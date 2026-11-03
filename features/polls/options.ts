import { cells, distinctTexts, refuse, type KindRules } from "../../server/core/kind";

/** One option as a player sees it; a poll answer is the option's index in this list. */
export type PollOption =
  | { kind: "choice"; text: string }
  | { kind: "not_applicable" }
  | { kind: "participant"; player: string }
  | { kind: "multiple" }
  | { kind: "none" };

export type PollKind = "poll_choice" | "poll_roster";
export const POLL_KINDS: readonly PollKind[] = ["poll_choice", "poll_roster"];

/** The options a question offers, as today: fixed choices plus "not applicable", or the players plus "several of us" and "none of us". */
export function optionsFor(kind: PollKind, payload: unknown, players: readonly string[]): { options: PollOption[]; notApplicable: number | null } {
  if (kind === "poll_roster") {
    return { options: [...players.map((player) => ({ kind: "participant" as const, player })), { kind: "multiple" }, { kind: "none" }], notApplicable: null };
  }
  const choices = (payload as { options: string[] }).options;
  return { options: [...choices.map((text) => ({ kind: "choice" as const, text })), { kind: "not_applicable" }], notApplicable: choices.length };
}

/** Today's voting rules: two to eight fixed options, or the players themselves. Poll questions have no level. */
const CHOICE_COLUMNS = ["choice1", "choice2", "choice3", "choice4", "choice5", "choice6", "choice7", "choice8"];

export const pollKinds: Record<PollKind, KindRules> = {
  poll_choice: {
    levelled: false,
    columns: CHOICE_COLUMNS,
    fromRow: (row) => ({ options: cells(row, CHOICE_COLUMNS) }),
    example: { prompt: "Tea or coffee?", choice1: "Tea", choice2: "Coffee", choice3: "", choice4: "", choice5: "", choice6: "", choice7: "", choice8: "" },
    check(payload) {
      const options = ((payload ?? {}) as Record<string, unknown>).options;
      if (!Array.isArray(options) || options.length < 2) return refuse("too_few_choices");
      if (options.length > 8) return refuse("too_many_choices");
      const texts = distinctTexts(options);
      return texts ? { ok: true, payload: { options: texts } } : refuse("bad_choices");
    },
  },
  poll_roster: {
    levelled: false,
    columns: [],
    // Choice cells on a roster row are read so that check refuses them rather than dropping them unseen.
    fromRow: (row) => ({ options: cells(row, CHOICE_COLUMNS) }),
    example: { prompt: "Who would survive longest on a desert island?" },
    check(payload) {
      const options = ((payload ?? {}) as Record<string, unknown>).options;
      return Array.isArray(options) && options.length ? refuse("players_has_choices") : { ok: true, payload: {} };
    },
  },
};
