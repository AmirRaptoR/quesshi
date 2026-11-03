import type { DuelRoomView } from "../types";

const runOf = (duel: DuelRoomView, me: string) => duel.runs?.find((run) => run.player === me);
const canPlay = (duel: DuelRoomView, me: string) => duel.phase === "running" && !runOf(duel, me)?.finished;

/** Today's two lists: the duels I can act on, and those waiting on someone else. A finished duel belongs to neither. */
export function split(duels: readonly DuelRoomView[], me: string) {
  const open = duels.filter((duel) => duel.phase !== "ended");
  return { yourTurn: open.filter((duel) => canPlay(duel, me)), waitingOnThem: open.filter((duel) => !canPlay(duel, me)) };
}

export function duelRoute(duel: DuelRoomView, me: string): string {
  if (duel.phase === "lobby") return `/lobby/${duel.code}`;
  return canPlay(duel, me) ? `/play/${duel.id}` : `/duel/${duel.id}`;
}

export const opponentOf = (duel: DuelRoomView, me: string) => duel.participants?.find((p) => p.id !== me) ?? null;

/** The line under a duel's name, as a translation key and its argument. */
export function standing(duel: DuelRoomView, me: string): [string, ...number[]] {
  if (!opponentOf(duel, me)) return ["home.waiting"];
  const run = runOf(duel, me);
  if (run?.finished) return ["home.youScored", run.score ?? 0];
  if (!run || run.answered === 0) return ["home.notStarted"];
  return ["home.questionsLeft", Math.max(0, (duel.total ?? 0) - run.answered)];
}
