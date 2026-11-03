import type { FeatureContext } from "../../server/core/feature";
import { drawQuestions, NotEnoughQuestions } from "../../server/core/questions";
import type { QuestionsStore, StoredQuestion } from "../../server/core/questions.store";
import type { RoomType } from "../../server/core/rooms";
import type { SettingsStore } from "./settings.store";
import type { SessionsStore } from "../../server/core/sessions.store";
import { DuelGame } from "./duel-game";
import { TRIVIA_KINDS, type TriviaItem } from "./items";
import { LiveGame, type LiveView } from "./live-game";

const QUESTION_COUNTS = [10, 20, 30, 40, 50, 100];
const DEFAULT_QUESTIONS = 10;
const LANGS = ["fa", "en", "nl"];

export interface GameSettings { lang: string; questions: number; categoryIds: string[]; levels: number[] }
interface Stores { sessions: SessionsStore; questions: QuestionsStore; settings: SettingsStore }

function draw(context: FeatureContext, stores: Stores, settings: GameSettings): StoredQuestion[] | null {
  try {
    return drawQuestions(stores.questions, {
      lang: settings.lang,
      categoryIds: context.categories.forGame(settings.categoryIds, settings.lang, TRIVIA_KINDS),
      levels: settings.levels,
      count: settings.questions,
      kinds: TRIVIA_KINDS,
    });
  } catch (error) {
    if (error instanceof NotEnoughQuestions) return null;
    throw error;
  }
}

const toItems = (drawn: StoredQuestion[]): TriviaItem[] =>
  drawn.map((q) => ({ prompt: q.prompt, level: q.level, explanation: q.explanation, kind: q.kind as TriviaItem["kind"], payload: q.payload }));

/** Live trivia: today's live duel; a lobby expires after ten minutes. */
export function liveRoomType(context: FeatureContext, stores: Stores, games: Map<string, LiveGame>): RoomType<GameSettings> {
  return {
    format: "live",
    get maxCapacity() {
      return stores.settings.get().maxCapacity;
    },
    closed: () => (stores.settings.get().liveEnabled ? null : "live_disabled"),
    lobbyLifetimeMs: 10 * 60_000,
    lobbyEnding: "lobby_expired",
    start(room, changed) {
      const drawn = draw(context, stores, room.settings);
      if (!drawn) return { ok: false, error: "not_enough_questions" };
      stores.sessions.start(room.id, room.participants, drawn.map((q) => q.id));
      let last: LiveView | null = null;
      const game = new LiveGame({
        sessionId: room.id, players: [...room.participants], items: toItems(drawn),
        clock: context.clock, schedule: context.schedule, random: context.random, sessions: stores.sessions,
        publish: (view) => {
          last = view;
          changed();
        },
      });
      games.set(room.id, game);
      game.start();
      return {
        ok: true,
        game: {
          leave: (player) => {
            game.leave(player);
            return true;
          },
          ended: () => (last?.phase === "ended" ? { reason: last.endedReason ?? "completed" } : null),
          end: () => game.end(),
          view: () => ({ game: last }),
        },
      };
    },
  };
}

/** The asynchronous duel; the duel, lobby included, is forfeited 48 hours after creation. */
export function duelRoomType(context: FeatureContext, stores: Stores, games: Map<string, DuelGame>): RoomType<GameSettings> {
  return {
    format: "async_duel",
    get maxCapacity() {
      return stores.settings.get().maxCapacity;
    },
    lobbyLifetimeMs: 48 * 3600_000,
    lobbyEnding: "forfeited",
    start(room, changed) {
      const drawn = draw(context, stores, room.settings);
      if (!drawn) return { ok: false, error: "not_enough_questions" };
      stores.sessions.start(room.id, room.participants, drawn.map((q) => q.id));
      const game = new DuelGame({
        sessionId: room.id, players: [...room.participants], items: toItems(drawn), createdAt: room.createdAt,
        clock: context.clock, schedule: context.schedule, random: context.random, sessions: stores.sessions, onChange: changed,
      });
      games.set(room.id, game);
      return {
        ok: true,
        game: {
          // A started duel cannot be left, as today.
          leave: () => false,
          ended: () => game.ended,
          end: () => game.end(),
          view: (viewer) => game.view(viewer ?? ""),
        },
      };
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
