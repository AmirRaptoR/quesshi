import type { Translator } from "../../../client/i18n";

/** One option as the server serves it; an answer is the option's index. */
export type PollOption =
  | { kind: "choice"; text: string }
  | { kind: "not_applicable" }
  | { kind: "participant"; player: string }
  | { kind: "multiple" }
  | { kind: "none" };

interface Named { id: string; displayName: string }

/**
 * Today's message under a closed question. "Not applicable" is counted in the distribution but is
 * never an answer to agree on, least of all when it is the viewer's own.
 */
export function agreementKey(slot: { counts: number[]; allAgreed: boolean } | null, notApplicable: number | null, ownNotApplicable: boolean): string {
  if (!slot || ownNotApplicable) return "voting.agreement.nothingToCompare";
  const real = slot.counts.reduce((sum, count) => sum + count, 0) - (notApplicable === null ? 0 : slot.counts[notApplicable] ?? 0);
  if (real < 2) return "voting.agreement.nothingToCompare";
  return slot.allAgreed ? "voting.agreement.allAgreed" : "voting.agreement.disagreed";
}

/** A pair with nothing comparable has no percentage; showing 0% would claim a disagreement. */
export const pairAgreement = (percent: number | null, t: Translator): string =>
  percent === null ? t.t("voting.results.nothingToCompare") : t.format("voting.results.agreementPercent", percent);

export function optionText(option: PollOption, players: readonly Named[], t: Translator): string {
  switch (option.kind) {
    case "choice": return option.text;
    case "participant": return players.find((player) => player.id === option.player)?.displayName ?? t.t("voting.answer.noAnswer");
    case "not_applicable": return t.t("voting.answer.notApplicable");
    case "multiple": return t.t(players.length === 2 ? "voting.answer.both" : "voting.answer.multiplePeople");
    case "none": return t.t("voting.answer.noOne");
  }
}

export function answerText(options: readonly PollOption[], answer: number | undefined, players: readonly Named[], t: Translator): string {
  const option = answer === undefined ? undefined : options[answer];
  return option ? optionText(option, players, t) : t.t("voting.answer.noAnswer");
}
