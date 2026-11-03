import type { AdminAuth } from "./admin";
import { adminOnly } from "./admin.routes";
import type { Clock } from "./clock";
import type { Feature } from "./feature";
import { checkQuestion, importTemplate, readImport } from "./question-input";
import type { QuestionFilter, QuestionsStore } from "./questions.store";

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
    // One route writes any kind, new or edited.
    .post("/questions", ({ body, set }) => {
      const input = (body ?? {}) as Record<string, unknown>;
      const checked = checkQuestion(input, kinds, questions.categories().map((c) => c.id));
      if (!checked.ok) return bad(set, checked.error);
      const id = typeof input.id === "string" ? input.id.trim() : "";
      if (id) {
        if (!questions.question(id)) return bad(set, "not_found", 404);
        questions.update({ id, ...checked.value });
        return questions.question(id);
      }
      const created = { id: crypto.randomUUID(), ...checked.value, createdAt: clock.now().toISOString() };
      questions.add(created);
      return questions.question(created.id);
    })
    // A dry run is the default: the admin sees every row's fate before anything is written.
    .post("/questions/import", ({ body, query, set }) => {
      const read = readImport(query.kind ?? "", query.format ?? "", typeof body === "string" ? body : "", kinds, questions.categories().map((c) => c.id));
      if (!read.ok) return bad(set, read.error);
      const dryRun = query.dryRun !== "false";
      if (!dryRun) {
        const createdAt = clock.now().toISOString();
        questions.addMany(read.value.questions.map((q) => ({ id: crypto.randomUUID(), ...q, createdAt })));
      }
      return { dryRun, ...read.value.report };
    }, { parse: "text" })
    .get("/questions/import/template", ({ query, set }) => {
      const entry = kinds.get(query.kind ?? "");
      if (!entry) return bad(set, "bad_kind");
      if (query.format !== "csv" && query.format !== "json") return bad(set, "bad_format");
      set.headers["content-type"] = query.format === "csv" ? "text/csv; charset=utf-8" : "application/json";
      set.headers["content-disposition"] = `attachment; filename="${query.kind}-template.${query.format}"`;
      return importTemplate(entry.rules, query.format);
    })
    .post("/questions/:id/approve", ({ params, set }) => (questions.setStatus(params.id, "approved") ? { ok: true } : bad(set, "not_found", 404)))
    .post("/questions/:id/reject", ({ params, set }) => (questions.setStatus(params.id, "rejected") ? { ok: true } : bad(set, "not_found", 404)))
    .delete("/questions/:id", ({ params, set }) => (questions.remove(params.id) ? { ok: true } : bad(set, "not_found", 404)));
}
