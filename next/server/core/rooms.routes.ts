import { Elysia } from "elysia";
import type { FeatureContext } from "./feature";
import type { Lang, Player } from "./identity.store";
import type { Rooms, RoomType } from "./rooms";

const LANGS: readonly Lang[] = ["fa", "en", "nl"];
type Set = { status?: number | string; headers: Record<string, unknown> };
export const fail = (set: Set, status: number, error: string) => {
  set.status = status;
  return { error };
};

export interface LobbyRoutesOptions<S> {
  context: FeatureContext;
  rooms: Rooms;
  type: RoomType<S>;
  /** The path segment under the feature's prefix, such as "live". */
  segment: string;
  readSettings(body: Record<string, unknown>): S | null;
  /** Handles a message a phone sends on the room's socket; returns the reply. */
  onMessage?(roomId: string, player: Player, message: Record<string, unknown>): unknown;
}

/** The lobby routes every game shares: open, join (also as a new guest), read, start, leave, settings, cancel, socket. */
export function lobbyRoutes<S>({ context, rooms, type, segment, readSettings, onMessage }: LobbyRoutesOptions<S>) {
  const format = type.format;
  return new Elysia()
    .resolve(({ request }) => ({ player: context.playerFor(request.headers.get("cookie")) }))
    .post(`/${segment}/lobby`, ({ player, body, set }) => {
      if (!player) return fail(set, 401, "signed_out");
      if (player.guest) return fail(set, 403, "guests_cannot_create");
      const closed = type.closed?.();
      if (closed) return fail(set, 503, closed);
      const raw = (body ?? {}) as Record<string, unknown>;
      const settings = readSettings(raw);
      const capacity = raw.capacity;
      if (!settings) return fail(set, 400, "invalid_settings");
      if (!Number.isInteger(capacity) || (capacity as number) < 2 || (capacity as number) > type.maxCapacity) return fail(set, 400, "bad_capacity");
      return rooms.open(type, player.id, settings, capacity as number);
    })
    .post(`/${segment}/join/:code`, ({ player, params, set }) => {
      if (!player) return fail(set, 401, "signed_out");
      const closed = type.closed?.();
      if (closed) return fail(set, 503, closed);
      const joined = rooms.join(params.code, format, player.id);
      return joined.ok ? { id: joined.id } : fail(set, joined.error === "not_found" ? 404 : 409, joined.error);
    })
    // Becoming a guest and taking the seat are one call, so a refused join never leaves a guest behind.
    .post(`/${segment}/guest/:code`, ({ params, body, set }) => {
      const { name, lang } = (body ?? {}) as { name?: unknown; lang?: unknown };
      const trimmed = typeof name === "string" ? name.trim() : "";
      if (trimmed.length < 2 || trimmed.length > 24) return fail(set, 400, "name_length");
      const closed = type.closed?.();
      if (closed) return fail(set, 503, closed);
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
        if (!player || !onMessage || typeof message !== "object" || message === null) {
          ws.send({ type: "error", error: "malformed_message" });
          return;
        }
        ws.send(onMessage(ws.data.params.id, player, message as Record<string, unknown>));
      },
    });
}
