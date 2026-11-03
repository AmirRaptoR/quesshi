import type { AdminAuth } from "./admin";
import { adminOnly } from "./admin.routes";
import { categoryIcon, slugify } from "./categories";
import type { QuestionsStore } from "./questions.store";

const text = (value: unknown) => (typeof value === "string" ? value.trim() : "");
const ids = (value: unknown) => (Array.isArray(value) ? value.filter((id): id is string => typeof id === "string") : []);

/** Category management and which categories each enabled feature offers, as today's admin pages. */
export function categoryAdminRoutes(auth: AdminAuth, questions: QuestionsStore, features: readonly string[]) {
  const settings = () => Object.fromEntries(features.map((feature) => [feature, questions.offers(feature)]));
  return adminOnly(auth, "/api/admin")
    .get("/categories", () => questions.categories())
    .post("/categories", ({ body, set }) => {
      const input = (body ?? {}) as Record<string, unknown>;
      const nameEn = text(input.nameEn);
      if (!nameEn) {
        set.status = 400;
        return { error: "bad_name" };
      }
      const id = slugify(text(input.id) || nameEn);
      if (!id) {
        set.status = 400;
        return { error: "bad_id" };
      }
      const icon = categoryIcon(text(input.icon));
      if (!icon) {
        set.status = 400;
        return { error: "bad_icon" };
      }
      const all = questions.categories();
      const given = typeof input.sortOrder === "number" && Number.isInteger(input.sortOrder) && input.sortOrder > 0 ? input.sortOrder : 0;
      const sortOrder = given || (all.find((c) => c.id === id)?.sortOrder ?? Math.max(0, ...all.map((c) => c.sortOrder)) + 1);
      questions.saveCategory({
        id, nameFa: text(input.nameFa), nameEn, nameNl: text(input.nameNl), icon, color: text(input.color), isActive: input.isActive !== false, sortOrder,
      });
      return { ok: true };
    })
    .delete("/categories/:id", ({ params, set }) => {
      // Deleting a category with questions behind it would orphan them; deactivate it instead.
      if (!questions.deleteCategory(params.id)) {
        set.status = 400;
        return { error: "category_in_use" };
      }
      return { ok: true };
    })
    .get("/content-settings", () => ({ categories: questions.categories(), settings: settings() }))
    .put("/content-settings", ({ body }) => {
      const input = (body ?? {}) as Record<string, unknown>;
      for (const feature of features) questions.setOffers(feature, ids(input[feature]));
      return settings();
    });
}
