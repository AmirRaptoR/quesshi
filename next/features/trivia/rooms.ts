import type { FeatureContext } from "../../server/core/feature";
import { drawQuestions, NotEnoughQuestions } from "../../server/core/questions";
import type { QuestionsStore } from "../../server/core/questions.store";
import type { SessionsStore } from "../../server/core/sessions.store";
import { DuelGame } from "./duel-game";
import { TRIVIA_KINDS, type TriviaItem } from "./items";
import { LiveGame, type LiveView } from "./live-game";

const QUESTION_COUNTS = [10, 20, 30, 40, 50, 100];
const DEFAULT_QUESTIONS = 10;
/** Today's default largest lobby; an admin setting comes with the admin pages. */
export const MAX_CAPACITY = 20;
const CODE_ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no I, O, 0 or 1: codes are read aloud
const LANGS = ["fa", "en", "nl"];
/** A live lobby expires after ten minutes; a duel, lobby included, is forfeited 48 hours after creation. */
const LOBBY_LIFETIME_MS = { live: 10 * 60_000, async_duel: 48 * 3600_000 };
const LOBBY_ENDING = { live: "lobby_expired", async_duel: "forfeited" };

export type Format = "live" | "async_duel";
export interface GameSettings { lang: string; questions: number; categoryIds: string[]; levels: number[] }

interface Room {
  id: string;
  format: Format;
  code: string;
  owner: string;
  capacity: number;
  settings: GameSettings;
  participants: string[];
  status: "lobby" | "running" | "ended";
  endedReason: string | null;
  createdAt: number;
  live: LiveGame | null;
  liveView: LiveView | null;
  duel: DuelGame | null;
  rematch: { id: string; code: string } | null;
  cancelExpiry: () => void;
}

export type JoinResult = { ok: true; id: string } | { ok: false; error: "not_found" | "full" | "started" };
export type StartResult = { ok: true } | { ok: false; error: "cannot_start" | "not_enough_questions" };

/** Today's lobby rules for live trivia and duels, and the games they become. Rooms live in memory. */
export function createRooms(context: FeatureContext, stores: { sessions: SessionsStore; questions: QuestionsStore }) {
  const rooms = new Map<string, Room>();
  const byCode = new Map<string, Room>();
  const topic = (id: string) => `trivia:room:${id}`;
  const now = () => context.clock.now().getTime();

  const gameEnded = (room: Room) => room.liveView?.phase === "ended" || room.duel?.ended != null;
  const view = (room: Room, viewer: string | null) => ({
    id: room.id,
    format: room.format,
    code: room.code,
    owner: room.owner,
    capacity: room.capacity,
    settings: room.settings,
    phase: gameEnded(room) ? "ended" : room.status,
    endedReason: room.endedReason ?? (room.duel?.ended?.reason ?? (room.liveView?.phase === "ended" ? "completed" : null)),
    lobbyEndsAt: room.createdAt + LOBBY_LIFETIME_MS[room.format],
    participants: room.participants.map((id) => {
      const player = context.player(id);
      return { id, displayName: player?.displayName ?? "", avatarSeed: player?.avatarSeed ?? id, guest: player?.guest ?? false };
    }),
    game: room.liveView,
    ...(room.duel ? room.duel.view(viewer ?? "") : {}),
    rematch: room.rematch,
  });
  const broadcast = (room: Room) => context.publish(topic(room.id), { type: "view", view: view(room, null) });

  const end = (room: Room, reason: string) => {
    room.status = "ended";
    room.endedReason = reason;
    room.cancelExpiry();
    byCode.delete(room.code);
    stores.sessions.finish({ sessionId: room.id, endedAt: new Date(now()).toISOString(), reason, result: null });
    broadcast(room);
  };

  const newCode = () => {
    for (;;) {
      const code = Array.from({ length: 6 }, () => CODE_ALPHABET[Math.floor(context.random() * CODE_ALPHABET.length)]).join("");
      if (!byCode.has(code)) return code;
    }
  };

  const open = (format: Format, owner: string, settings: GameSettings, capacity: number): { id: string; code: string } => {
    const room: Room = {
      id: crypto.randomUUID(), format, code: newCode(), owner, capacity, settings, participants: [owner], status: "lobby", endedReason: null,
      createdAt: now(), live: null, liveView: null, duel: null, rematch: null, cancelExpiry: () => {},
    };
    rooms.set(room.id, room);
    byCode.set(room.code, room);
    stores.sessions.createLobby({ id: room.id, code: room.code, format, createdAt: new Date(room.createdAt).toISOString() });
    room.cancelExpiry = context.schedule(room.createdAt + LOBBY_LIFETIME_MS[format], () => {
      if (room.status === "lobby") end(room, LOBBY_ENDING[format]);
    });
    return { id: room.id, code: room.code };
  };

  const room = (id: string, format: Format) => {
    const found = rooms.get(id);
    return found?.format === format ? found : undefined;
  };

  return {
    topic,
    open,
    view: (id: string, format: Format, viewer: string | null) => {
      const found = room(id, format);
      return found ? view(found, viewer) : null;
    },
    idForCode: (code: string, format: Format) => {
      const found = byCode.get(code.trim().toUpperCase());
      return found?.format === format ? found.id : null;
    },

    join(code: string, format: Format, player: string): JoinResult {
      const found = byCode.get(code.trim().toUpperCase());
      if (!found || found.format !== format) return { ok: false, error: "not_found" };
      if (found.participants.includes(player)) return { ok: true, id: found.id };
      if (found.status !== "lobby") return { ok: false, error: "started" };
      if (found.participants.length >= found.capacity) return { ok: false, error: "full" };
      found.participants.push(player);
      broadcast(found);
      return { ok: true, id: found.id };
    },

    leave(id: string, format: Format, player: string): boolean {
      const found = room(id, format);
      if (!found || !found.participants.includes(player)) return false;
      if (found.live) {
        found.live.leave(player);
        return true;
      }
      // The owner cannot leave a lobby; they cancel it. A started duel cannot be left, as today.
      if (found.status !== "lobby" || player === found.owner) return false;
      found.participants = found.participants.filter((candidate) => candidate !== player);
      broadcast(found);
      return true;
    },

    cancel(id: string, format: Format, player: string): boolean {
      const found = room(id, format);
      if (!found || found.status !== "lobby" || player !== found.owner) return false;
      end(found, "owner_cancelled");
      return true;
    },

    start(id: string, format: Format, player: string): StartResult {
      const found = room(id, format);
      if (!found || found.status !== "lobby" || player !== found.owner || found.participants.length < 2) return { ok: false, error: "cannot_start" };
      let drawn;
      try {
        drawn = drawQuestions(stores.questions, {
          lang: found.settings.lang,
          categoryIds: found.settings.categoryIds.length ? found.settings.categoryIds : null,
          levels: found.settings.levels,
          count: found.settings.questions,
          kinds: TRIVIA_KINDS,
        });
      } catch (error) {
        if (error instanceof NotEnoughQuestions) return { ok: false, error: "not_enough_questions" };
        throw error;
      }
      found.status = "running";
      stores.sessions.start(found.id, found.participants);
      const items: TriviaItem[] = drawn.map((q) => ({ prompt: q.prompt, level: q.level, explanation: q.explanation, kind: q.kind as TriviaItem["kind"], payload: q.payload }));
      const shared = { sessionId: found.id, players: found.participants, items, clock: context.clock, schedule: context.schedule, random: context.random, sessions: stores.sessions };
      if (format === "live") {
        found.cancelExpiry();
        found.live = new LiveGame({
          ...shared,
          publish: (game) => {
            found.liveView = game;
            // The code stays valid while the game runs, so a late joiner hears "started", not "not found".
            if (game.phase === "ended") byCode.delete(found.code);
            broadcast(found);
          },
        });
        found.live.start();
      } else {
        // The duel takes over the 48-hour forfeit from the lobby.
        found.cancelExpiry();
        found.duel = new DuelGame({ ...shared, createdAt: found.createdAt, onChange: () => {
          if (found.duel?.ended) byCode.delete(found.code);
          broadcast(found);
        } });
        broadcast(found);
      }
      return { ok: true };
    },

    updateSettings(id: string, format: Format, player: string, settings: GameSettings, capacity: number | null): boolean {
      const found = room(id, format);
      if (!found || found.status !== "lobby" || player !== found.owner) return false;
      if (capacity !== null && (capacity < 2 || capacity > MAX_CAPACITY || capacity < found.participants.length)) return false;
      found.settings = settings;
      if (capacity !== null) found.capacity = capacity;
      broadcast(found);
      return true;
    },

    /** One rematch lobby per finished live game, owned by the original owner with the same settings. */
    rematch(id: string, player: string): { id: string; code: string } | null {
      const found = room(id, "live");
      if (!found || !gameEnded(found) || !found.participants.includes(player) || context.player(player)?.guest) return null;
      if (!found.rematch) {
        found.rematch = open("live", found.owner, found.settings, found.capacity);
        broadcast(found);
      }
      return found.rematch;
    },

    liveAnswer(id: string, player: string, slot: number, answer: unknown) {
      const found = room(id, "live");
      if (!found?.live) return { ok: false as const, error: "not_accepted" as const };
      return found.live.answer(player, slot, answer);
    },

    duel(id: string, player: string): DuelGame | null {
      const found = room(id, "async_duel");
      return found?.duel && found.participants.includes(player) ? found.duel : null;
    },
  };
}

/** Reads game settings from a request, coercing an unknown question count to today's default. */
export function readSettings(body: Record<string, unknown>): GameSettings | null {
  const { lang, questions, categoryIds, levels } = body;
  if (typeof lang !== "string" || !LANGS.includes(lang)) return null;
  if (!Array.isArray(categoryIds) || !categoryIds.every((id) => typeof id === "string")) return null;
  if (!Array.isArray(levels) || !levels.every((level) => Number.isInteger(level) && level >= 1 && level <= 5)) return null;
  const count = QUESTION_COUNTS.includes(questions as number) ? (questions as number) : DEFAULT_QUESTIONS;
  return { lang, questions: count, categoryIds, levels };
}
