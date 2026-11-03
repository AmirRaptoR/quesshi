import { Elysia } from "elysia";
import { defineFeature, type FeatureContext } from "../../server/core/feature";
import type { Lang } from "../../server/core/identity.store";
import { createQuestionsStore } from "../../server/core/questions.store";
import { createSessionsStore } from "../../server/core/sessions.store";
import { createLiveRooms, MAX_CAPACITY, readSettings } from "./live-rooms";

const LANGS: readonly Lang[] = ["fa", "en", "nl"];

function liveRoutes(context: FeatureContext) {
  const rooms = createLiveRooms(context, { sessions: createSessionsStore(context.db), questions: createQuestionsStore(context.db) });
  const fail = (set: { status?: number | string }, status: number, error: string) => {
    set.status = status;
    return { error };
  };

  return new Elysia({ prefix: "/f/trivia" })
    .resolve(({ request }) => ({ player: context.playerFor(request.headers.get("cookie")) }))
    .post("/live/lobby", ({ player, body, set }) => {
      if (!player) return fail(set, 401, "signed_out");
      if (player.guest) return fail(set, 403, "guests_cannot_create");
      const raw = (body ?? {}) as Record<string, unknown>;
      const settings = readSettings(raw);
      const capacity = raw.capacity;
      if (!settings) return fail(set, 400, "invalid_settings");
      if (!Number.isInteger(capacity) || (capacity as number) < 2 || (capacity as number) > MAX_CAPACITY) return fail(set, 400, "bad_capacity");
      return rooms.open(player.id, settings, capacity as number);
    })
    .post("/live/join/:code", ({ player, params, set }) => {
      if (!player) return fail(set, 401, "signed_out");
      const joined = rooms.join(params.code, player.id);
      return joined.ok ? { id: joined.id } : fail(set, joined.error === "not_found" ? 404 : 409, joined.error);
    })
    // Becoming a guest and taking the seat are one call, so a refused join never leaves a guest behind.
    .post("/live/guest/:code", ({ params, body, set }) => {
      const { name, lang } = (body ?? {}) as { name?: unknown; lang?: unknown };
      const trimmed = typeof name === "string" ? name.trim() : "";
      if (trimmed.length < 2 || trimmed.length > 24) return fail(set, 400, "name_length");
      const id = rooms.idForCode(params.code);
      if (!id) return fail(set, 404, "not_found");
      if (rooms.view(id)?.phase !== "lobby") return fail(set, 409, "started");
      const guest = context.signInGuest(trimmed, LANGS.includes(lang as Lang) ? (lang as Lang) : "fa");
      const joined = rooms.join(params.code, guest.player.id);
      if (!joined.ok) return fail(set, 409, joined.error);
      set.headers["set-cookie"] = guest.cookie;
      return { id: joined.id };
    })
    .get("/live/:id", ({ params, set }) => rooms.view(params.id) ?? fail(set, 404, "not_found"))
    .post("/live/:id/start", ({ player, params, set }) => {
      if (!player) return fail(set, 401, "signed_out");
      const started = rooms.start(params.id, player.id);
      if (started.ok) return { started: true };
      return fail(set, started.error === "not_enough_questions" ? 503 : 400, started.error);
    })
    .post("/live/:id/leave", ({ player, params, set }) => {
      if (!player) return fail(set, 401, "signed_out");
      return rooms.leave(params.id, player.id) ? { left: true } : fail(set, 400, "cannot_leave");
    })
    .put("/live/:id/settings", ({ player, params, body, set }) => {
      if (!player) return fail(set, 401, "signed_out");
      const raw = (body ?? {}) as Record<string, unknown>;
      const settings = readSettings(raw);
      const capacity = raw.capacity === undefined || raw.capacity === null ? null : raw.capacity;
      if (!settings || (capacity !== null && !Number.isInteger(capacity))) return fail(set, 400, "invalid_settings");
      return rooms.updateSettings(params.id, player.id, settings, capacity as number | null) ? { updated: true } : fail(set, 400, "cannot_update_settings");
    })
    .post("/live/:id/rematch", ({ player, params, set }) => {
      if (!player) return fail(set, 401, "signed_out");
      return rooms.rematch(params.id, player.id) ?? fail(set, 400, "refused");
    })
    .delete("/live/:id", ({ player, params, set }) => {
      if (!player) return fail(set, 401, "signed_out");
      return rooms.cancel(params.id, player.id) ? { cancelled: true } : fail(set, 400, "cannot_cancel");
    })
    .ws("/live/:id/socket", {
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
        const current = rooms.view(id);
        if (current) ws.send({ type: "view", view: current });
      },
      message(ws, message) {
        const player = ws.data.player;
        const { type, slot, answer } = (message ?? {}) as { type?: unknown; slot?: unknown; answer?: unknown };
        if (!player || type !== "answer" || !Number.isInteger(slot)) {
          ws.send({ type: "error", error: "malformed_message" });
          return;
        }
        const result = rooms.answer(ws.data.params.id, player.id, slot as number, answer);
        ws.send(result.ok ? { type: "answered", slot, answer: result.answer } : { type: "error", error: result.error });
      },
    });
}

/** Trivia: the choice, order and map question kinds, played live or as an asynchronous duel. */
export default defineFeature({
  name: "trivia",
  server: liveRoutes,
});
