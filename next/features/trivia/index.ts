import { Elysia } from "elysia";
import { defineFeature, type FeatureContext } from "../../server/core/feature";
import { createQuestionsStore } from "../../server/core/questions.store";
import { fail, lobbyRoutes } from "../../server/core/rooms.routes";
import { createSessionsStore } from "../../server/core/sessions.store";
import type { DuelGame } from "./duel-game";
import type { LiveGame } from "./live-game";
import { kinds, TRIVIA_KINDS } from "./items";
import { createSettingsStore, type TriviaSettings } from "./settings.store";
import { duelRoomType, liveRoomType, readSettings } from "./rooms";

function triviaRoutes(context: FeatureContext) {
  const settings = createSettingsStore(context.db);
  const stores = { sessions: createSessionsStore(context.db), questions: createQuestionsStore(context.db), settings };
  const liveGames = new Map<string, LiveGame>();
  const duels = new Map<string, DuelGame>();
  const live = liveRoomType(context, stores, liveGames);
  const duel = duelRoomType(context, stores, duels);
  const { rooms } = context;

  return new Elysia({ prefix: "/f/trivia" })
    .get("/categories", () => context.categories.offered(TRIVIA_KINDS))
    .get("/limits", () => settings.get())
    // Which trivia room a game code opens, so a lobby page can live at the code it shares.
    .get("/code/:code", ({ params, set }) => {
      for (const format of ["live", "async_duel"]) {
        const id = rooms.idForCode(params.code, format);
        if (id) return { id, format };
      }
      return fail(set, 404, "not_found");
    })
    .get("/admin/settings", ({ request, set }) => (context.isAdmin(request.headers.get("cookie")) ? settings.get() : fail(set, 401, "signed_out")))
    .put("/admin/settings", ({ request, body, set }) => {
      if (!context.isAdmin(request.headers.get("cookie"))) return fail(set, 401, "signed_out");
      const { liveEnabled, maxCapacity } = (body ?? {}) as Record<string, unknown>;
      const changes: Partial<TriviaSettings> = {};
      if (liveEnabled !== undefined) {
        if (typeof liveEnabled !== "boolean") return fail(set, 400, "bad_live_enabled");
        changes.liveEnabled = liveEnabled;
      }
      if (maxCapacity !== undefined) {
        // Today's bounds: two players at least, five hundred at most.
        if (!Number.isInteger(maxCapacity) || (maxCapacity as number) < 2 || (maxCapacity as number) > 500) return fail(set, 400, "bad_capacity");
        changes.maxCapacity = maxCapacity as number;
      }
      settings.save(changes);
      return settings.get();
    })
    .use(lobbyRoutes({
      context, rooms, type: live, segment: "live", readSettings,
      // A live game takes answers on its socket.
      onMessage(roomId, player, { type, slot, answer }) {
        const game = liveGames.get(roomId);
        if (type !== "answer" || !Number.isInteger(slot) || !game) return { type: "error", error: "malformed_message" };
        const result = game.answer(player.id, slot as number, answer);
        return result.ok ? { type: "answered", slot, answer: result.answer } : { type: "error", error: result.error };
      },
    }))
    .use(lobbyRoutes({ context, rooms, type: duel, segment: "duel", readSettings }))
    .resolve(({ request }) => ({ player: context.playerFor(request.headers.get("cookie")) }))
    .get("/duels", ({ player, set }) => (player ? rooms.ofPlayer(player.id, "async_duel") : fail(set, 401, "signed_out")))
    // One rematch lobby per finished live game, owned by the original owner with the same settings.
    .post("/live/:id/rematch", ({ player, params, set }) => {
      if (!player) return fail(set, 401, "signed_out");
      const room = rooms.room(params.id, "live");
      if (!room || !room.ended || !room.info.participants.includes(player.id) || player.guest) return fail(set, 400, "refused");
      if (!room.extra.rematch) room.set("rematch", rooms.open(live, room.info.owner, room.info.settings, room.info.capacity));
      return room.extra.rematch;
    })
    .post("/duel/:id/next", ({ player, params, set }) => {
      if (!player) return fail(set, 401, "signed_out");
      const game = duels.get(params.id);
      if (!game || !rooms.room(params.id, "async_duel")?.info.participants.includes(player.id)) return fail(set, 404, "not_found");
      return game.next(player.id) ?? fail(set, 409, "nothing_to_serve");
    })
    .post("/duel/:id/answer", ({ player, params, body, set }) => {
      if (!player) return fail(set, 401, "signed_out");
      const game = duels.get(params.id);
      if (!game || !rooms.room(params.id, "async_duel")?.info.participants.includes(player.id)) return fail(set, 404, "not_found");
      const { slot, answer } = (body ?? {}) as { slot?: unknown; answer?: unknown };
      if (!Number.isInteger(slot)) return fail(set, 400, "malformed_answer");
      const result = game.answer(player.id, slot as number, answer ?? null);
      return "error" in result ? fail(set, 400, result.error) : result;
    });
}

/** Trivia: the choice, order and map question kinds, played live or as an asynchronous duel. */
export default defineFeature({
  name: "trivia",
  kinds,
  migrations: new URL("./migrations/", import.meta.url),
  server: triviaRoutes,
});
