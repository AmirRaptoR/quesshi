import type { Translator } from "../../../../client/i18n";
import type { DuelRoomView, RoomView } from "../types";
import { remainingSeconds } from "./clock";

const seated = (room: RoomView) => room.participants ?? [];
export const isOwner = (room: RoomView, me: string) => room.owner === me;
export const isParticipant = (room: RoomView, me: string) => seated(room).some((p) => p.id === me);
export const canStart = (room: RoomView, me: string) => isOwner(room, me) && seated(room).length >= 2;
export const isFull = (room: RoomView) => seated(room).length >= room.capacity;
export const openSeatCount = (room: RoomView) => Math.max(0, room.capacity - seated(room).length);
export const capacityStepMin = (room: RoomView) => Math.max(2, seated(room).length);
export const rosterColumns = (room: RoomView) => (room.capacity <= 3 ? 3 : 4);

/** Where a game that has left its lobby is played: a live screen, or a duel's next question or result. */
export function targetRoute(room: DuelRoomView, me: string): string {
  if (room.format === "live") return `/live/${room.id}`;
  const run = room.runs?.find((r) => r.player === me);
  return run && !run.finished && room.phase === "running" ? `/play/${room.id}` : `/duel/${room.id}`;
}

/** A lobby's remaining time as a duration: minutes rounded up, so it never says "0 min" with time left. */
export function expiryText(endsAt: number, now: number, t: Translator): string {
  const seconds = remainingSeconds(endsAt, now);
  return seconds >= 60 ? t.format("lobby.closesInMinutes", Math.ceil(seconds / 60)) : t.format("lobby.closesIn", seconds);
}

/** The path segment of a format's routes under /f/trivia. */
export const segment = (format: string) => (format === "live" ? "live" : "duel");
