import { Elysia } from "elysia";
import type { Player } from "./identity.store";
import { readSetImport, type KindEntry } from "./question-input";
import type { QuestionSet, QuestionSetsStore } from "./question-sets.store";

const LANGS = ["fa", "en", "nl"] as const;
const MAX_NAME = 60;
const text = (value: unknown) => (typeof value === "string" ? value.trim() : "");

/** A signed-in member's private question sets; guests and other players' sets are out of reach. */
export function questionSetRoutes(sets: QuestionSetsStore, playerFor: (cookie: string | null) => Player | null, now: () => Date, newId: () => string, kinds: ReadonlyMap<string, KindEntry>) {
  return new Elysia({ prefix: "/api/question-sets" })
    .resolve(({ request }) => ({ player: playerFor(request.headers.get("cookie")) }))
    .onBeforeHandle(({ player, set }) => {
      if (!player) {
        set.status = 401;
        return { error: "signed_out" };
      }
      if (player.guest) {
        set.status = 403;
        return { error: "guest" };
      }
    })
    .get("/", ({ player }) => sets.list(player!.id))
    .post("/", ({ player, body, set }) => {
      const input = (body ?? {}) as Record<string, unknown>;
      const name = text(input.name);
      if (!name || name.length > MAX_NAME) {
        set.status = 400;
        return { error: "bad_name" };
      }
      const lang = text(input.lang);
      if (!LANGS.some((l) => l === lang)) {
        set.status = 400;
        return { error: "bad_lang" };
      }
      const id = newId();
      if (sets.create({ id, ownerId: player!.id, name, lang: lang as QuestionSet["lang"], createdAt: now().toISOString() }) === "limit") {
        set.status = 400;
        return { error: "too_many_sets" };
      }
      return sets.set(id);
    })
    .patch("/:id", ({ player, params, body, set }) => {
      const owned = sets.set(params.id);
      if (owned?.ownerId !== player!.id) return notFound(set);
      const name = text((body as { name?: unknown } | null)?.name);
      if (!name || name.length > MAX_NAME) {
        set.status = 400;
        return { error: "bad_name" };
      }
      sets.rename(owned.id, name);
      return sets.set(owned.id);
    })
    .delete("/:id", ({ player, params, set }) => {
      if (sets.set(params.id)?.ownerId !== player!.id) return notFound(set);
      sets.remove(params.id);
      return { ok: true };
    })
    .get("/:id/questions", ({ player, params, set }) => {
      if (sets.set(params.id)?.ownerId !== player!.id) return notFound(set);
      return sets.questions(params.id);
    })
    .post("/:id/import", ({ player, params, body, set }) => {
      const owned = sets.set(params.id);
      if (owned?.ownerId !== player!.id) return notFound(set);
      const pasted = (body as { text?: unknown } | null)?.text;
      const read = readSetImport(typeof pasted === "string" ? pasted : "", owned.lang, kinds);
      if (!read.ok) {
        set.status = read.error === "file_too_large" ? 413 : 400;
        return { error: read.error };
      }
      const { added } = sets.append(owned.id, read.value.questions.map(({ question }) => ({ ...question, id: newId() })), now().toISOString());
      const full = read.value.questions.slice(added).map(({ row }) => ({ row, reason: "set_full" }));
      return { accepted: added, rejected: [...read.value.rejected, ...full].sort((a, b) => a.row - b.row) };
    })
    .delete("/:id/questions/:questionId", ({ player, params, set }) => {
      if (sets.set(params.id)?.ownerId !== player!.id || !sets.removeQuestion(params.id, params.questionId)) return notFound(set);
      return { ok: true };
    });
}

function notFound(set: { status?: number | string }) {
  set.status = 404;
  return { error: "not_found" };
}
