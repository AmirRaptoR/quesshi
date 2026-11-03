import type { FeatureContext } from "../../server/core/feature";
import { drawQuestions, NotEnoughQuestions } from "../../server/core/questions";
import type { QuestionsStore } from "../../server/core/questions.store";
import type { SessionsStore } from "../../server/core/sessions.store";
import { LiveGame, type LiveView, type TriviaItem } from "./live-game";

const LOBBY_MINUTES = 10;
const QUESTION_COUNTS = [10, 20, 30, 40, 50, 100];
const DEFAULT_QUESTIONS = 10;
/** Today's default largest lobby; an admin setting comes with the admin pages. */
export const MAX_CAPACITY = 20;
const CODE_ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no I, O, 0 or 1: codes are read aloud
const TRIVIA_KINDS = ["choice", "order", "map_pick"] as const;
const LANGS = ["fa", "en", "nl"];

export interface LiveSettings { lang: string; questions: number; categoryIds: string[]; levels: number[] }
type Status = "lobby" | "running" | "ended";

interface Room {
  id: string;
  code: string;
  owner: string;
  capacity: number;
  settings: LiveSettings;
  participants: string[];
  status: Status;
  endedReason: string | null;
  createdAt: number;
  game: LiveGame | null;
  lastView: LiveView | null;
  cancelExpiry: () => void;
}

export type JoinResult = { ok: true; id: string } | { ok: false; error: "not_found" | "full" | "started" };
export type StartResult = { ok: true } | { ok: false; error: "cannot_start" | "not_enough_questions" };

/** Today's lobby rules for live trivia, and the games they become. Rooms live in memory. */
export function createLiveRooms(context: FeatureContext, stores: { sessions: SessionsStore; questions: QuestionsStore }) {
  const rooms = new Map<string, Room>();
  const byCode = new Map<string, Room>();
  const topic = (id: string) => `trivia:live:${id}`;
  const now = () => context.clock.now().getTime();

  const view = (room: Room) => ({
    id: room.id,
    code: room.code,
    owner: room.owner,
    capacity: room.capacity,
    settings: room.settings,
    phase: room.lastView?.phase === "ended" ? "ended" : room.status,
    endedReason: room.endedReason ?? (room.lastView?.phase === "ended" ? "completed" : null),
    lobbyEndsAt: room.createdAt + LOBBY_MINUTES * 60_000,
    participants: room.participants.map((id) => {
      const player = context.player(id);
      return { id, displayName: player?.displayName ?? "", avatarSeed: player?.avatarSeed ?? id, guest: player?.guest ?? false };
    }),
    game: room.lastView,
  });
  const broadcast = (room: Room) => context.publish(topic(room.id), { type: "view", view: view(room) });

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

  return {
    topic,
    view: (id: string) => {
      const room = rooms.get(id);
      return room ? view(room) : null;
    },
    idForCode: (code: string) => byCode.get(code.trim().toUpperCase())?.id ?? null,

    open(owner: string, settings: LiveSettings, capacity: number): { id: string; code: string } {
      const room: Room = {
        id: crypto.randomUUID(), code: newCode(), owner, capacity, settings, participants: [owner],
        status: "lobby", endedReason: null, createdAt: now(), game: null, lastView: null, cancelExpiry: () => {},
      };
      rooms.set(room.id, room);
      byCode.set(room.code, room);
      stores.sessions.createLobby({ id: room.id, code: room.code, format: "live", createdAt: new Date(room.createdAt).toISOString() });
      room.cancelExpiry = context.schedule(room.createdAt + LOBBY_MINUTES * 60_000, () => {
        if (room.status === "lobby") end(room, "lobby_expired");
      });
      return { id: room.id, code: room.code };
    },

    join(code: string, player: string): JoinResult {
      const room = byCode.get(code.trim().toUpperCase());
      if (!room) return { ok: false, error: "not_found" };
      if (room.participants.includes(player)) return { ok: true, id: room.id };
      if (room.status !== "lobby") return { ok: false, error: "started" };
      if (room.participants.length >= room.capacity) return { ok: false, error: "full" };
      room.participants.push(player);
      broadcast(room);
      return { ok: true, id: room.id };
    },

    leave(id: string, player: string): boolean {
      const room = rooms.get(id);
      if (!room || !room.participants.includes(player)) return false;
      if (room.game) {
        room.game.leave(player);
        return true;
      }
      // The owner cannot leave a lobby; they cancel it.
      if (room.status !== "lobby" || player === room.owner) return false;
      room.participants = room.participants.filter((candidate) => candidate !== player);
      broadcast(room);
      return true;
    },

    cancel(id: string, player: string): boolean {
      const room = rooms.get(id);
      if (!room || room.status !== "lobby" || player !== room.owner) return false;
      end(room, "owner_cancelled");
      return true;
    },

    start(id: string, player: string): StartResult {
      const room = rooms.get(id);
      if (!room || room.status !== "lobby" || player !== room.owner || room.participants.length < 2) return { ok: false, error: "cannot_start" };
      let drawn;
      try {
        drawn = drawQuestions(stores.questions, {
          lang: room.settings.lang,
          categoryIds: room.settings.categoryIds.length ? room.settings.categoryIds : null,
          levels: room.settings.levels,
          count: room.settings.questions,
          kinds: TRIVIA_KINDS,
        });
      } catch (error) {
        if (error instanceof NotEnoughQuestions) return { ok: false, error: "not_enough_questions" };
        throw error;
      }
      room.status = "running";
      room.cancelExpiry();
      stores.sessions.start(room.id, room.participants);
      const items: TriviaItem[] = drawn.map((q) => ({ prompt: q.prompt, level: q.level, explanation: q.explanation, kind: q.kind as TriviaItem["kind"], payload: q.payload }));
      room.game = new LiveGame({
        sessionId: room.id, players: room.participants, items,
        clock: context.clock, schedule: context.schedule, random: context.random, sessions: stores.sessions,
        publish: (game) => {
          room.lastView = game;
          // The code stays valid while the game runs, so a late joiner hears "started", not "not found".
          if (game.phase === "ended") byCode.delete(room.code);
          broadcast(room);
        },
      });
      room.game.start();
      return { ok: true };
    },

    answer(id: string, player: string, slot: number, answer: unknown) {
      const room = rooms.get(id);
      if (!room?.game) return { ok: false as const, error: "not_accepted" as const };
      return room.game.answer(player, slot, answer);
    },
  };
}

/** Reads lobby settings from a request, coercing an unknown question count to today's default. */
export function readSettings(body: Record<string, unknown>): LiveSettings | null {
  const { lang, questions, categoryIds, levels } = body;
  if (typeof lang !== "string" || !LANGS.includes(lang)) return null;
  if (!Array.isArray(categoryIds) || !categoryIds.every((id) => typeof id === "string")) return null;
  if (!Array.isArray(levels) || !levels.every((level) => Number.isInteger(level) && level >= 1 && level <= 5)) return null;
  const count = QUESTION_COUNTS.includes(questions as number) ? (questions as number) : DEFAULT_QUESTIONS;
  return { lang, questions: count, categoryIds, levels };
}
