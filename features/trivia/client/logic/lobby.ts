import type { DuelRoomView } from "../types";

/** Where a game that has left its lobby is played: a live screen, or a duel's next question or result. */
export function targetRoute(room: DuelRoomView, me: string): string {
  if (room.format === "live") return `/live/${room.id}`;
  const run = room.runs?.find((r) => r.player === me);
  return run && !run.finished && room.phase === "running" ? `/play/${room.id}` : `/duel/${room.id}`;
}

/** The path segment of a format's routes under /f/trivia. */
export const segment = (format: string) => (format === "live" ? "live" : "duel");
