import { Elysia } from "elysia";
import { defineFeature, type FeatureContext } from "../../server/core/feature";
import { drawQuestions, NotEnoughQuestions } from "../../server/core/questions";
import { createQuestionsStore } from "../../server/core/questions.store";
import type { RoomType } from "../../server/core/rooms";
import { fail, lobbyRoutes } from "../../server/core/rooms.routes";
import { createSessionsStore } from "../../server/core/sessions.store";
import { POLL_KINDS, pollKinds, type PollKind } from "./options";
import { PollGame } from "./poll-game";

const QUESTION_COUNTS = [10, 20, 30, 40, 50, 100];
const LANGS = ["fa", "en", "nl"];
/** Today's voting lobby holds two to eight players. */
const MAX_CAPACITY = 8;

interface PollSettings { lang: string; questions: number; categoryIds: string[] }

function readSettings(body: Record<string, unknown>): PollSettings | null {
  const { lang, questions, categoryIds } = body;
  if (typeof lang !== "string" || !LANGS.includes(lang)) return null;
  if (!Array.isArray(categoryIds) || !categoryIds.every((id) => typeof id === "string")) return null;
  return { lang, questions: QUESTION_COUNTS.includes(questions as number) ? (questions as number) : 10, categoryIds };
}

function pollRoutes(context: FeatureContext) {
  const sessions = createSessionsStore(context.db);
  const questions = createQuestionsStore(context.db);
  const games = new Map<string, PollGame>();
  const type: RoomType<PollSettings> = {
    format: "poll",
    maxCapacity: MAX_CAPACITY,
    lobbyLifetimeMs: 48 * 3600_000,
    lobbyEnding: "lobby_expired",
    start(room, changed) {
      let drawn;
      try {
        drawn = drawQuestions(questions, {
          lang: room.settings.lang, categoryIds: context.categories.forGame(room.settings.categoryIds, room.settings.lang, POLL_KINDS),
          levels: null, count: room.settings.questions, kinds: POLL_KINDS,
        });
      } catch (error) {
        if (error instanceof NotEnoughQuestions) return { ok: false, error: "not_enough_questions" };
        throw error;
      }
      sessions.start(room.id, room.participants, drawn.map((q) => q.id));
      const game = new PollGame({
        sessionId: room.id, players: [...room.participants], items: drawn.map((q) => ({ prompt: q.prompt, kind: q.kind as PollKind, payload: q.payload })),
        clock: context.clock, schedule: context.schedule, sessions, onChange: changed,
      });
      games.set(room.id, game);
      return { ok: true, game: { leave: (player) => game.leave(player), ended: () => game.ended, end: () => game.end(), view: (viewer) => game.view(viewer) } };
    },
  };

  return new Elysia({ prefix: "/f/polls" })
    .get("/categories", () => context.categories.offered(POLL_KINDS))
    .use(lobbyRoutes({ context, rooms: context.rooms, type, segment: "poll", readSettings }))
    .resolve(({ request }) => ({ player: context.playerFor(request.headers.get("cookie")) }))
    .post("/poll/:id/answer", ({ player, params, body, set }) => {
      if (!player) return fail(set, 401, "signed_out");
      const game = games.get(params.id);
      if (!game) return fail(set, 404, "not_found");
      const { slot, option } = (body ?? {}) as { slot?: unknown; option?: unknown };
      if (!Number.isInteger(slot) || !Number.isInteger(option)) return fail(set, 400, "not_accepted");
      return game.answer(player.id, slot as number, option as number) ? { answered: true } : fail(set, 400, "not_accepted");
    });
}

/** Polls: ungraded choice and roster questions, answered together and shown as counts and agreement. */
export default defineFeature({
  name: "polls",
  kinds: pollKinds,
  server: pollRoutes,
});
