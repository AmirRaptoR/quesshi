import type { FeatureContext } from "./feature";
import type { SessionsStore } from "./sessions.store";

/** How long an ended room stays in memory, so players can still read its result and ask for a rematch. */
const ENDED_ROOM_RETENTION_MS = 24 * 3600_000;
const CODE_ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no I, O, 0 or 1: codes are read aloud

/** A started game, as the lobby sees it. */
export interface GameHandle {
  /** A player leaves a running game; false when the format does not allow it. */
  leave(player: string): boolean;
  /** An administrator ends the game at once, as a no contest. */
  end(): void;
  /** The ending, once the game is over. */
  ended(): { reason: string } | null;
  /**
   * What one viewer (null: everyone) may see of the game; merged into the room view. `full` is false
   * for broadcasts, which go to every phone on every change and so carry only what changes often.
   */
  view(viewer: string | null, full: boolean): Record<string, unknown>;
}

export interface RoomInfo<S> { id: string; code: string; owner: string; participants: readonly string[]; settings: S; createdAt: number }

/** How one kind of game uses a lobby. */
export interface RoomType<S> {
  format: string;
  maxCapacity: number;
  /** Why the format takes no new lobbies or joins right now, or null when it does. */
  closed?(): string | null;
  /** How long an unstarted lobby stays open, and what its ending is called when it runs out. */
  lobbyLifetimeMs: number;
  lobbyEnding: string;
  /** Starts the game; `changed` is called after every change so the room is re-broadcast. */
  start(room: RoomInfo<S>, changed: () => void): { ok: true; game: GameHandle } | { ok: false; error: "not_enough_questions" };
}

interface Room {
  type: RoomType<unknown>;
  id: string;
  code: string;
  owner: string;
  capacity: number;
  settings: unknown;
  participants: string[];
  status: "lobby" | "running" | "ended";
  endedReason: string | null;
  createdAt: number;
  game: GameHandle | null;
  extra: Record<string, unknown>;
  cancelExpiry: () => void;
  forgetting: boolean;
  flushQueued: boolean;
}

export type JoinResult = { ok: true; id: string } | { ok: false; error: "not_found" | "full" | "started" };
export type StartResult = { ok: true } | { ok: false; error: "cannot_start" | "not_enough_questions" };
export type Rooms = ReturnType<typeof createRooms>;

/**
 * Today's lobby rules, shared by every game: a code, an owner, a capacity, joining until the start,
 * the owner starts (two or more players), changes settings or cancels; others may leave. Rooms live
 * in memory; their sessions are recorded in the database.
 */
export function createRooms(context: Pick<FeatureContext, "clock" | "schedule" | "random" | "player" | "publish">, sessions: SessionsStore) {
  const rooms = new Map<string, Room>();
  const byCode = new Map<string, Room>();
  const topic = (id: string) => `room:${id}`;
  const now = () => context.clock.now().getTime();

  const ended = (room: Room) => room.status === "ended" || room.game?.ended() != null;
  const view = (room: Room, viewer: string | null, full: boolean) => ({
    id: room.id,
    format: room.type.format,
    code: room.code,
    owner: room.owner,
    capacity: room.capacity,
    settings: room.settings,
    phase: ended(room) ? "ended" : room.status,
    endedReason: room.endedReason ?? room.game?.ended()?.reason ?? null,
    lobbyEndsAt: room.createdAt + room.type.lobbyLifetimeMs,
    // A running game's roster no longer changes much, and with hundreds of phones it would be most
    // of every broadcast; phones keep the roster from their first full view.
    ...(full || room.status === "lobby" ? {
      participants: room.participants.map((id) => {
        const player = context.player(id);
        return { id, displayName: player?.displayName ?? "", avatarSeed: player?.avatarSeed ?? id, guest: player?.guest ?? false };
      }),
    } : {}),
    ...(room.game ? room.game.view(viewer, full) : {}),
    ...room.extra,
  });
  const broadcast = (room: Room) => {
    // The code stays valid while the game runs, so a late joiner hears "started", not "not found".
    if (ended(room)) {
      byCode.delete(room.code);
      if (!room.forgetting) {
        room.forgetting = true;
        context.schedule(now() + ENDED_ROOM_RETENTION_MS, () => rooms.delete(room.id));
      }
    }
    // Every change in one turn of the event loop goes out as one message, so a burst of answers in a
    // big game costs one broadcast instead of one per answer.
    if (room.flushQueued) return;
    room.flushQueued = true;
    setTimeout(() => {
      room.flushQueued = false;
      context.publish(topic(room.id), { type: "view", view: view(room, null, false) });
    }, 0);
  };

  const end = (room: Room, reason: string) => {
    room.status = "ended";
    room.endedReason = reason;
    room.cancelExpiry();
    sessions.finish({ sessionId: room.id, endedAt: new Date(now()).toISOString(), reason, result: null });
    broadcast(room);
  };

  const newCode = () => {
    for (;;) {
      const code = Array.from({ length: 6 }, () => CODE_ALPHABET[Math.floor(context.random() * CODE_ALPHABET.length)]).join("");
      if (!byCode.has(code)) return code;
    }
  };

  const find = (id: string, format: string) => {
    const room = rooms.get(id);
    return room?.type.format === format ? room : undefined;
  };

  return {
    topic,

    open<S>(type: RoomType<S>, owner: string, settings: S, capacity: number): { id: string; code: string } {
      const room: Room = {
        type: type as RoomType<unknown>, id: crypto.randomUUID(), code: newCode(), owner, capacity, settings, participants: [owner],
        status: "lobby", endedReason: null, createdAt: now(), game: null, extra: {}, cancelExpiry: () => {}, forgetting: false, flushQueued: false,
      };
      rooms.set(room.id, room);
      byCode.set(room.code, room);
      sessions.createLobby({ id: room.id, code: room.code, format: type.format, createdAt: new Date(room.createdAt).toISOString() });
      room.cancelExpiry = context.schedule(room.createdAt + type.lobbyLifetimeMs, () => {
        if (room.status === "lobby") end(room, type.lobbyEnding);
      });
      return { id: room.id, code: room.code };
    },

    view(id: string, format: string, viewer: string | null) {
      const room = find(id, format);
      return room ? view(room, viewer, true) : null;
    },

    idForCode(code: string, format: string): string | null {
      const room = byCode.get(code.trim().toUpperCase());
      return room?.type.format === format ? room.id : null;
    },

    join(code: string, format: string, player: string): JoinResult {
      const room = byCode.get(code.trim().toUpperCase());
      if (!room || room.type.format !== format) return { ok: false, error: "not_found" };
      if (room.participants.includes(player)) return { ok: true, id: room.id };
      if (room.status !== "lobby") return { ok: false, error: "started" };
      if (room.participants.length >= room.capacity) return { ok: false, error: "full" };
      room.participants.push(player);
      broadcast(room);
      return { ok: true, id: room.id };
    },

    leave(id: string, format: string, player: string): boolean {
      const room = find(id, format);
      if (!room || !room.participants.includes(player)) return false;
      if (room.game) return room.game.leave(player);
      // The owner cannot leave a lobby; they cancel it.
      if (room.status !== "lobby" || player === room.owner) return false;
      room.participants = room.participants.filter((candidate) => candidate !== player);
      broadcast(room);
      return true;
    },

    cancel(id: string, format: string, player: string): boolean {
      const room = find(id, format);
      if (!room || room.status !== "lobby" || player !== room.owner) return false;
      end(room, "owner_cancelled");
      return true;
    },

    start(id: string, format: string, player: string): StartResult {
      const room = find(id, format);
      if (!room || room.status !== "lobby" || player !== room.owner || room.participants.length < 2) return { ok: false, error: "cannot_start" };
      const started = room.type.start(
        { id: room.id, code: room.code, owner: room.owner, participants: room.participants, settings: room.settings, createdAt: room.createdAt },
        () => broadcast(room),
      );
      if (!started.ok) return started;
      room.status = "running";
      room.cancelExpiry();
      room.game = started.game;
      broadcast(room);
      return { ok: true };
    },

    updateSettings(id: string, format: string, player: string, settings: unknown, capacity: number | null): boolean {
      const room = find(id, format);
      if (!room || room.status !== "lobby" || player !== room.owner) return false;
      if (capacity !== null && (capacity < 2 || capacity > room.type.maxCapacity || capacity < room.participants.length)) return false;
      room.settings = settings;
      if (capacity !== null) room.capacity = capacity;
      broadcast(room);
      return true;
    },

    /** Every open lobby and running game, oldest first, as everyone sees them. */
    all() {
      return [...rooms.values()].filter((room) => !ended(room)).sort((a, b) => a.createdAt - b.createdAt).map((room) => view(room, null, false));
    },

    /** The rooms of a format a player sits in, still kept in memory, newest first, as that player sees them. */
    ofPlayer(player: string, format: string) {
      return [...rooms.values()]
        .filter((room) => room.type.format === format && room.participants.includes(player))
        .sort((a, b) => b.createdAt - a.createdAt)
        .map((room) => view(room, player));
    },

    /** An administrator ends a lobby or a running game; false when it is unknown or already over. */
    endByAdmin(id: string): boolean {
      const room = rooms.get(id);
      if (!room || ended(room)) return false;
      if (room.game) {
        room.game.end();
        broadcast(room);
      } else end(room, "ended_by_admin");
      return true;
    },

    /** The room's facts and game, for format-specific actions. */
    room(id: string, format: string) {
      const room = find(id, format);
      if (!room) return null;
      return {
        info: { id: room.id, code: room.code, owner: room.owner, participants: room.participants, settings: room.settings, createdAt: room.createdAt, capacity: room.capacity },
        game: room.game,
        ended: ended(room),
        /** Adds fields to the room's view, such as a rematch, and re-broadcasts it. */
        set(key: string, value: unknown) {
          room.extra[key] = value;
          broadcast(room);
        },
        extra: room.extra,
      };
    },
  };
}
