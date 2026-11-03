import { Elysia } from "elysia";
import { defineFeature, type FeatureContext } from "../../server/core/feature";
import type { Lang, Player } from "../../server/core/identity.store";
import { createQuestionsStore } from "../../server/core/questions.store";
import { createSessionsStore } from "../../server/core/sessions.store";
import { createRooms, MAX_CAPACITY, readSettings, type Format } from "./rooms";

const LANGS: readonly Lang[] = ["fa", "en", "nl"];
type Set = { status?: number | string; headers: Record<string, unknown> };
const fail = (set: Set, status: number, error: string) => {
  set.status = status;
  return { error };
};

function triviaRoutes(context: FeatureContext) {
  const rooms = createRooms(context, { sessions: createSessionsStore(context.db), questions: createQuestionsStore(context.db) });
  const app = new Elysia({ prefix: "/f/trivia" }).resolve(({ request }) => ({ player: context.playerFor(request.headers.get("cookie")) }));

  /** The lobby routes live trivia and duels share, under /f/trivia/<segment>. */
  const lobby = (segment: string, format: Format) => app
    .post(`/${segment}/lobby`, ({ player, body, set }) => {
      if (!player) return fail(set, 401, "signed_out");
      if (player.guest) return fail(set, 403, "guests_cannot_create");
      const raw = (body ?? {}) as Record<string, unknown>;
      const settings = readSettings(raw);
      const capacity = raw.capacity;
      if (!settings) return fail(set, 400, "invalid_settings");
      if (!Number.isInteger(capacity) || (capacity as number) < 2 || (capacity as number) > MAX_CAPACITY) return fail(set, 400, "bad_capacity");
      return rooms.open(format, player.id, settings, capacity as number);
    })
    .post(`/${segment}/join/:code`, ({ player, params, set }) => {
      if (!player) return fail(set, 401, "signed_out");
      const joined = rooms.join(params.code, format, player.id);
      return joined.ok ? { id: joined.id } : fail(set, joined.error === "not_found" ? 404 : 409, joined.error);
    })
    // Becoming a guest and taking the seat are one call, so a refused join never leaves a guest behind.
    .post(`/${segment}/guest/:code`, ({ params, body, set }) => {
      const { name, lang } = (body ?? {}) as { name?: unknown; lang?: unknown };
      const trimmed = typeof name === "string" ? name.trim() : "";
      if (trimmed.length < 2 || trimmed.length > 24) return fail(set, 400, "name_length");
      const id = rooms.idForCode(params.code, format);
      if (!id) return fail(set, 404, "not_found");
      if (rooms.view(id, format, null)?.phase !== "lobby") return fail(set, 409, "started");
      const guest = context.signInGuest(trimmed, LANGS.includes(lang as Lang) ? (lang as Lang) : "fa");
      const joined = rooms.join(params.code, format, guest.player.id);
      if (!joined.ok) return fail(set, 409, joined.error);
      set.headers["set-cookie"] = guest.cookie;
      return { id: joined.id };
    })
    .get(`/${segment}/:id`, ({ player, params, set }) => rooms.view(params.id, format, player?.id ?? null) ?? fail(set, 404, "not_found"))
    .post(`/${segment}/:id/start`, ({ player, params, set }) => {
      if (!player) return fail(set, 401, "signed_out");
      const started = rooms.start(params.id, format, player.id);
      if (started.ok) return { started: true };
      return fail(set, started.error === "not_enough_questions" ? 503 : 400, started.error);
    })
    .post(`/${segment}/:id/leave`, ({ player, params, set }) => {
      if (!player) return fail(set, 401, "signed_out");
      return rooms.leave(params.id, format, player.id) ? { left: true } : fail(set, 400, "cannot_leave");
    })
    .put(`/${segment}/:id/settings`, ({ player, params, body, set }) => {
      if (!player) return fail(set, 401, "signed_out");
      const raw = (body ?? {}) as Record<string, unknown>;
      const settings = readSettings(raw);
      const capacity = raw.capacity === undefined || raw.capacity === null ? null : raw.capacity;
      if (!settings || (capacity !== null && !Number.isInteger(capacity))) return fail(set, 400, "invalid_settings");
      return rooms.updateSettings(params.id, format, player.id, settings, capacity as number | null) ? { updated: true } : fail(set, 400, "cannot_update_settings");
    })
    .delete(`/${segment}/:id`, ({ player, params, set }) => {
      if (!player) return fail(set, 401, "signed_out");
      return rooms.cancel(params.id, format, player.id) ? { cancelled: true } : fail(set, 400, "cannot_cancel");
    })
    // Phones follow a room over a socket; a live game also takes answers on it.
    .ws(`/${segment}/:id/socket`, {
      // A socket carries the cookie, so another site must not be able to open one.
      beforeHandle({ request, player, set }) {
        if (request.headers.get("origin") !== context.origin || !player) {
          set.status = 403;
          return "forbidden";
        }
      },
      open(ws) {
        const { id } = ws.data.params;
        ws.subscribe(rooms.topic(id));
        const current = rooms.view(id, format, ws.data.player?.id ?? null);
        if (current) ws.send({ type: "view", view: current });
      },
      message(ws, message) {
        const player = ws.data.player as Player | null;
        const { type, slot, answer } = (message ?? {}) as { type?: unknown; slot?: unknown; answer?: unknown };
        if (format !== "live" || !player || type !== "answer" || !Number.isInteger(slot)) {
          ws.send({ type: "error", error: "malformed_message" });
          return;
        }
        const result = rooms.liveAnswer(ws.data.params.id, player.id, slot as number, answer);
        ws.send(result.ok ? { type: "answered", slot, answer: result.answer } : { type: "error", error: result.error });
      },
    });

  lobby("live", "live");
  lobby("duel", "async_duel");

  return app
    .post("/live/:id/rematch", ({ player, params, set }) => {
      if (!player) return fail(set, 401, "signed_out");
      return rooms.rematch(params.id, player.id) ?? fail(set, 400, "refused");
    })
    .post("/duel/:id/next", ({ player, params, set }) => {
      if (!player) return fail(set, 401, "signed_out");
      const duel = rooms.duel(params.id, player.id);
      if (!duel) return fail(set, 404, "not_found");
      return duel.next(player.id) ?? fail(set, 409, "nothing_to_serve");
    })
    .post("/duel/:id/answer", ({ player, params, body, set }) => {
      if (!player) return fail(set, 401, "signed_out");
      const duel = rooms.duel(params.id, player.id);
      if (!duel) return fail(set, 404, "not_found");
      const { slot, answer } = (body ?? {}) as { slot?: unknown; answer?: unknown };
      if (!Number.isInteger(slot)) return fail(set, 400, "malformed_answer");
      const result = duel.answer(player.id, slot as number, answer ?? null);
      return "error" in result ? fail(set, 400, result.error) : result;
    });
}

/** Trivia: the choice, order and map question kinds, played live or as an asynchronous duel. */
export default defineFeature({
  name: "trivia",
  server: triviaRoutes,
});
