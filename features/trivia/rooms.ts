import type { FeatureContext } from "../../server/core/feature";
import { drawQuestions, NotEnoughQuestions } from "../../server/core/questions";
import type { QuestionsStore, StoredQuestion } from "../../server/core/questions.store";
import type { Player } from "../../server/core/identity.store";
import type { GameHandle, RoomType } from "../../server/core/rooms";
import type { SettingsStore } from "./settings.store";
import type { SessionsStore } from "../../server/core/sessions.store";
import { DuelGame, type DuelSnapshot } from "./duel-game";
import { TRIVIA_KINDS, type TriviaItem } from "./items";
import { broadcastView, LiveGame, type LiveSnapshot, type LiveView } from "./live-game";

const QUESTION_COUNTS = [10, 20, 30, 40, 50, 100];
const DEFAULT_QUESTIONS = 10;
const LANGS = ["fa", "en", "nl"];

/** With a questionSetId the game draws from that set alone, and the categories and levels do not apply. */
export interface GameSettings { lang: string; questions: number; categoryIds: string[]; levels: number[]; questionSetId?: string }
interface Stores { sessions: SessionsStore; questions: QuestionsStore; settings: SettingsStore }

function draw(context: FeatureContext, stores: Stores, settings: GameSettings): StoredQuestion[] | null {
  try {
    if (settings.questionSetId) return context.questionSets.draw(settings.questionSetId, settings.questions, TRIVIA_KINDS);
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
  drawn.map((q) => ({ id: q.id, categoryId: q.categoryId, prompt: q.prompt, level: q.level, explanation: q.explanation, kind: q.kind as TriviaItem["kind"], payload: q.payload, media: q.media ?? null }));

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
      const live = runLive(room.id, changed, (publish) => new LiveGame({ ...deps(room.id, room.participants, toItems(drawn)), publish }));
      live.game.start();
      return { ok: true, game: live.handle };
    },
    resume(room, saved, changed) {
      const snapshot = saved as LiveSnapshot;
      const live = runLive(room.id, changed, (publish) => new LiveGame({ ...deps(room.id, snapshot.players, snapshot.items), publish }, snapshot));
      live.game.resume();
      return live.handle;
    },
  };

  function deps(sessionId: string, players: readonly string[], items: TriviaItem[]) {
    return { sessionId, players: [...players], items, clock: context.clock, schedule: context.schedule, random: context.random, sessions: stores.sessions };
  }

  function runLive(id: string, changed: () => void, create: (publish: (view: LiveView) => void) => LiveGame): { game: LiveGame; handle: GameHandle } {
    let last: LiveView | null = null;
    const game = create((view) => {
      last = view;
      changed();
    });
    games.set(id, game);
    return {
      game,
      handle: {
        leave: (player) => {
          game.leave(player);
          return true;
        },
        ended: () => (last?.phase === "ended" ? { reason: last.endedReason ?? "completed" } : null),
        end: () => game.end(),
        snapshot: () => game.snapshot(),
        // Results go with a full view, and with the ending, which every phone needs once.
        view: (_viewer, full) => ({
          game: !last ? last : full || last.phase === "ended" ? { ...last, results: game.results() } : broadcastView(last),
        }),
      },
    };
  }
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
      return { ok: true, game: runDuel(room.id, new DuelGame({ ...deps(room.id, room.participants, toItems(drawn), room.createdAt), onChange: changed })) };
    },
    resume(room, saved, changed) {
      const snapshot = saved as DuelSnapshot;
      return runDuel(room.id, new DuelGame({ ...deps(room.id, snapshot.players, snapshot.items, snapshot.createdAt), onChange: changed }, snapshot));
    },
  };

  function deps(sessionId: string, players: readonly string[], items: TriviaItem[], createdAt: number) {
    return { sessionId, players: [...players], items, createdAt, clock: context.clock, schedule: context.schedule, random: context.random, sessions: stores.sessions };
  }

  function runDuel(id: string, game: DuelGame): GameHandle {
    games.set(id, game);
    return {
      // A started duel cannot be left, as today.
      leave: () => false,
      ended: () => game.ended,
      end: () => game.end(),
      snapshot: () => game.snapshot(),
      view: (viewer) => game.view(viewer ?? ""),
    };
  }
}

/** Reads game settings from a request, coercing an unknown question count to today's default. */
export function readSettings(context: FeatureContext, body: Record<string, unknown>, player: Player): GameSettings | null {
  const { lang, questions, categoryIds, levels, questionSetId } = body;
  if (typeof lang !== "string" || !LANGS.includes(lang)) return null;
  if (!Array.isArray(categoryIds) || !categoryIds.every((id) => typeof id === "string")) return null;
  if (!Array.isArray(levels) || !levels.every((level) => Number.isInteger(level) && level >= 1 && level <= 5)) return null;
  const count = QUESTION_COUNTS.includes(questions as number) ? (questions as number) : DEFAULT_QUESTIONS;
  if (questionSetId === undefined || questionSetId === null) return { lang, questions: count, categoryIds, levels };
  // Another player's set, or one that does not exist, is refused alike; the set's own language rules.
  const owned = typeof questionSetId === "string" ? context.questionSets.ownedBy(questionSetId, player.id) : null;
  return owned ? { lang: owned.lang, questions: count, categoryIds: [], levels: [], questionSetId: owned.id } : null;
}
