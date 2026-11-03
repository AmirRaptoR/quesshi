import type { Standing } from "../types";

/** The live results headline, from the ending, my standing and who left. */
export function headlineKey(reason: string | null, standings: readonly Standing[] | null, seats: readonly { player: string; status: string }[], me: string): string {
  if (reason !== "completed") return "live.end.noContest";
  if (seats.find((seat) => seat.player === me)?.status === "left") return "live.end.abandonedByYou";
  if (seats.some((seat) => seat.player !== me && seat.status === "left")) return "live.end.abandonedByThem";
  const outcome = standings?.find((s) => s.player === me)?.outcome;
  return outcome === "win" ? "result.win" : outcome === "draw" ? "result.draw" : "result.loss";
}

export const outcomeKey = (outcome: string) => (outcome === "win" ? "result.outcome.win" : outcome === "draw" ? "result.outcome.draw" : "result.outcome.loss");
export const chipClass = (outcome: string) => (outcome === "win" ? "chip--win" : outcome === "draw" ? "chip--draw" : "chip--loss");
