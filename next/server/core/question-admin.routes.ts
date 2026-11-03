import type { AdminAuth } from "./admin";
import { adminOnly } from "./admin.routes";
import type { Clock } from "./clock";
import type { Feature } from "./feature";
import type { QuestionFilter, QuestionsStore } from "./questions.store";

const LANGS = ["fa", "en", "nl"];
const STATUSES = ["pending", "approved", "rejected"];

const text = (value: unknown) => (typeof value === "string" ? value.trim() : "");
const optional = (value: string | undefined) => (value?.trim() ? value.trim() : undefined);
const whole = (value: string | undefined, fallback: number) => (value && /^\d+$/.test(value) ? Number(value) : fallback);

/** Today's question bank administration, for the kinds of every enabled feature. */
export function questionAdminRoutes(auth: AdminAuth, questions: QuestionsStore, features: readonly Feature[], clock: Clock) {
  const kinds = new Map(features.flatMap((feature) => Object.entries(feature.kinds ?? {}).map(([kind, rules]) => [kind, { feature: feature.name, rules }] as const)));
  const bad = (set: { status?: number | string }, error: string, status = 400) => {
    set.status = status;
    return { error };
  };

  return adminOnly(auth, "/api/admin")
    .get("/question-kinds", () => [...kinds].map(([kind, { feature, rules }]) => ({ kind, feature, levelled: rules.levelled })))
    .get("/questions", ({ query }) => {
      const level = whole(query.level, 0);
      const filter: QuestionFilter = {
        skip: whole(query.skip, 0),
        take: Math.min(Math.max(whole(query.take, 25), 1), 100),
      };
      for (const [key, value] of [["lang", query.lang], ["categoryId", query.category], ["status", query.status], ["kind", query.kind], ["text", query.text]] as const) {
        const given = optional(value);
        if (given) filter[key] = given;
      }
      if (level) filter.level = level;
      return questions.find(filter);
    })
    // One route writes any kind, new or edited; each rejection is a code the form translates and shows by its field.
    .post("/questions", ({ body, set }) => {
      const input = (body ?? {}) as Record<string, unknown>;
      const kind = kinds.get(text(input.kind));
      if (!kind) return bad(set, "bad_kind");
      const lang = text(input.lang);
      if (!LANGS.includes(lang)) return bad(set, "bad_lang");
      const level = input.level ?? null;
      const levelOk = kind.rules.levelled ? Number.isInteger(level) && (level as number) >= 1 && (level as number) <= 5 : level === null;
      if (!levelOk) return bad(set, "bad_level");
      const prompt = text(input.prompt);
      if (!prompt) return bad(set, "bad_prompt");
      const categoryId = optional(typeof input.categoryId === "string" ? input.categoryId : undefined) ?? null;
      if (categoryId && !questions.categories().some((c) => c.id === categoryId)) return bad(set, "bad_category");
      const status = input.status === undefined ? "pending" : text(input.status);
      if (!STATUSES.includes(status)) return bad(set, "bad_status");
      const checked = kind.rules.check(input.payload);
      if (!checked.ok) return bad(set, checked.error);

      const fields = {
        kind: text(input.kind), lang, categoryId, level: level as number | null, prompt, payload: checked.payload,
        explanation: optional(typeof input.explanation === "string" ? input.explanation : undefined) ?? null, status,
      };
      const id = text(input.id);
      if (id) {
        if (!questions.question(id)) return bad(set, "not_found", 404);
        questions.update({ id, ...fields });
        return questions.question(id);
      }
      const created = { id: crypto.randomUUID(), ...fields, createdAt: clock.now().toISOString() };
      questions.add(created);
      return questions.question(created.id);
    })
    .post("/questions/:id/approve", ({ params, set }) => (questions.setStatus(params.id, "approved") ? { ok: true } : bad(set, "not_found", 404)))
    .post("/questions/:id/reject", ({ params, set }) => (questions.setStatus(params.id, "rejected") ? { ok: true } : bad(set, "not_found", 404)))
    .delete("/questions/:id", ({ params, set }) => (questions.remove(params.id) ? { ok: true } : bad(set, "not_found", 404)));
}
