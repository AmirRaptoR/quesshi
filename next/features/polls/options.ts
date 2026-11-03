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
