/** A seated player, as every room view lists them. */
export interface Seat { id: string; displayName: string; avatarSeed: string; guest: boolean }

/** The core's room view (GET /f/<feature>/<segment>/:id and the room socket); a game adds its own fields. */
export interface RoomView {
  id: string;
  format: string;
  code: string;
  owner: string;
  capacity: number;
  settings: unknown;
  phase: "lobby" | "running" | "ended";
  endedReason: string | null;
  lobbyEndsAt: number;
  participants: Seat[];
}

export const isOwner = (room: RoomView, me: string) => room.owner === me;
/** Only decides whether Start is enabled; the server applies the same rule. */
export const canStart = (room: RoomView, me: string) => isOwner(room, me) && room.participants.length >= 2;
export const isParticipant = (room: RoomView, me: string) => room.participants.some((seat) => seat.id === me);
export const isFull = (room: RoomView) => room.participants.length >= room.capacity;
/** Capacity never drops below who is seated, nor below two. */
export const capacityFloor = (room: RoomView) => Math.max(2, room.participants.length);
export const openSeats = (room: RoomView) => Math.max(0, room.capacity - room.participants.length);
export const seatState = (room: RoomView, player: string): "host" | "ready" => (player === room.owner ? "host" : "ready");
/** Three generous columns for a small lobby; a fourth keeps eight seats on one phone screen. */
export const rosterColumns = (capacity: number) => (capacity <= 3 ? 3 : 4);
