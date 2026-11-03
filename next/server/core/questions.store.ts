import type { Db } from "./db";

export interface StoredQuestion {
  id: string;
  kind: string;
  lang: string;
  categoryId: string | null;
  level: number | null;
  prompt: string;
  payload: unknown;
  explanation: string | null;
}

interface QuestionRow { id: string; kind: string; lang: string; category_id: string | null; level: number | null; prompt: string; payload: string; explanation: string | null }
const toQuestion = (row: QuestionRow): StoredQuestion => ({
  id: row.id, kind: row.kind, lang: row.lang, categoryId: row.category_id, level: row.level, prompt: row.prompt, payload: JSON.parse(row.payload), explanation: row.explanation,
});

export interface AdminQuestion extends StoredQuestion { status: string; createdAt: string }
export interface QuestionFilter { lang?: string; categoryId?: string; level?: number; status?: string; kind?: string; text?: string; skip: number; take: number }

export interface Category { id: string; nameFa: string; nameEn: string; nameNl: string; icon: string; color: string; isActive: boolean; sortOrder: number }

interface CategoryRow { id: string; name_fa: string; name_en: string; name_nl: string; icon: string; color: string; active: number; sort_order: number }
const toCategory = (row: CategoryRow): Category => ({
  id: row.id, nameFa: row.name_fa, nameEn: row.name_en, nameNl: row.name_nl, icon: row.icon, color: row.color, isActive: row.active === 1, sortOrder: row.sort_order,
});

export type QuestionsStore = ReturnType<typeof createQuestionsStore>;

/** The question bank and its categories. */
export function createQuestionsStore(db: Db) {
  return {
    addCategory(category: { id: string; nameFa: string; nameEn: string; nameNl: string; icon: string; color: string }): void {
      db.run("insert into categories (id, name_fa, name_en, name_nl, icon, color) values (?, ?, ?, ?, ?, ?)", [category.id, category.nameFa, category.nameEn, category.nameNl, category.icon, category.color]);
    },
    categories(): Category[] {
      return db.query<CategoryRow, []>("select * from categories order by sort_order, id").all().map(toCategory);
    },
    saveCategory(c: Category): void {
      db.run(
        `insert into categories (id, name_fa, name_en, name_nl, icon, color, active, sort_order) values (?, ?, ?, ?, ?, ?, ?, ?)
         on conflict (id) do update set name_fa = excluded.name_fa, name_en = excluded.name_en, name_nl = excluded.name_nl,
           icon = excluded.icon, color = excluded.color, active = excluded.active, sort_order = excluded.sort_order`,
        [c.id, c.nameFa, c.nameEn, c.nameNl, c.icon, c.color, c.isActive ? 1 : 0, c.sortOrder],
      );
    },
    /** Deletes a category no question uses; its offers go with it. Returns false when questions still use it. */
    deleteCategory(id: string): boolean {
      return db.transaction(() => {
        if (db.query<{ n: number }, [string]>("select count(*) as n from questions where category_id = ?").get(id)!.n > 0) return false;
        db.run("delete from categories where id = ?", [id]);
        return true;
      })();
    },
    /** The categories a feature offers, in category order. */
    offers(feature: string): string[] {
      return db.query<{ id: string }, [string]>(
        "select categories.id from category_offers join categories on categories.id = category_id where feature = ? order by sort_order, categories.id",
      ).all(feature).map((row) => row.id);
    },
    /** The active categories a feature offers, with the languages that have approved questions of its kinds. */
    offeredCategories(feature: string, kinds: readonly string[]): (Category & { langs: string[] })[] {
      const rows = db.query<CategoryRow, [string]>(
        "select categories.* from category_offers join categories on categories.id = category_id where feature = ? and active = 1 order by sort_order, categories.id",
      ).all(feature);
      const langs = db.query<{ category_id: string; lang: string }, string[]>(
        `select distinct category_id, lang from questions where status = 'approved' and kind in (${kinds.map(() => "?").join(",")}) order by lang`,
      ).all(...kinds);
      return rows.map((row) => ({ ...toCategory(row), langs: langs.filter((l) => l.category_id === row.id).map((l) => l.lang) }));
    },
    setOffers(feature: string, categoryIds: readonly string[]): void {
      db.transaction(() => {
        db.run("delete from category_offers where feature = ?", [feature]);
        for (const id of categoryIds) db.run("insert or ignore into category_offers (feature, category_id) select ?, id from categories where id = ?", [feature, id]);
      })();
    },
    add(question: StoredQuestion & { status: string; createdAt: string }): void {
      db.run(
        "insert into questions (id, kind, lang, category_id, level, prompt, payload, explanation, status, created_at) values (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
        [question.id, question.kind, question.lang, question.categoryId, question.level, question.prompt, JSON.stringify(question.payload), question.explanation, question.status, question.createdAt],
      );
    },
    question(id: string): AdminQuestion | null {
      const row = db.query<QuestionRow & { status: string; created_at: string }, [string]>("select * from questions where id = ?").get(id);
      return row ? { ...toQuestion(row), status: row.status, createdAt: row.created_at } : null;
    },
    /** The admin's question list, newest first. */
    find(filter: QuestionFilter): { items: AdminQuestion[]; total: number } {
      const where: string[] = [];
      const params: (string | number)[] = [];
      const match = (sql: string, value: string | number | undefined) => {
        if (value === undefined) return;
        where.push(sql);
        params.push(value);
      };
      match("lang = ?", filter.lang);
      match("category_id = ?", filter.categoryId);
      match("level = ?", filter.level);
      match("status = ?", filter.status);
      match("kind = ?", filter.kind);
      match("instr(lower(prompt), lower(?)) > 0", filter.text);
      const clause = where.length ? `where ${where.join(" and ")}` : "";
      const total = db.query<{ n: number }, (string | number)[]>(`select count(*) as n from questions ${clause}`).get(...params)!.n;
      const rows = db.query<QuestionRow & { status: string; created_at: string }, (string | number)[]>(
        `select * from questions ${clause} order by created_at desc, id limit ? offset ?`,
      ).all(...params, filter.take, filter.skip);
      return { items: rows.map((row) => ({ ...toQuestion(row), status: row.status, createdAt: row.created_at })), total };
    },
    update(question: StoredQuestion & { status: string }): void {
      db.run(
        "update questions set kind = ?, lang = ?, category_id = ?, level = ?, prompt = ?, payload = ?, explanation = ?, status = ? where id = ?",
        [question.kind, question.lang, question.categoryId, question.level, question.prompt, JSON.stringify(question.payload), question.explanation, question.status, question.id],
      );
    },
    setStatus(id: string, status: string): boolean {
      return db.run("update questions set status = ? where id = ?", [status, id]).changes > 0;
    },
    remove(id: string): boolean {
      return db.run("delete from questions where id = ?", [id]).changes > 0;
    },
    /** One random approved question matching the filter, or null. A null level matches any level. */
    randomApproved(filter: { lang: string; kinds: readonly string[]; categoryIds: readonly string[] | null; level: number | null; exclude: readonly string[] }): StoredQuestion | null {
      const where = ["status = 'approved'", "lang = ?", `kind in (${filter.kinds.map(() => "?").join(",")})`];
      const params: (string | number)[] = [filter.lang, ...filter.kinds];
      if (filter.level !== null) {
        where.push("level = ?");
        params.push(filter.level);
      }
      if (filter.categoryIds) {
        where.push(`category_id in (${filter.categoryIds.map(() => "?").join(",")})`);
        params.push(...filter.categoryIds);
      }
      if (filter.exclude.length) {
        where.push(`id not in (${filter.exclude.map(() => "?").join(",")})`);
        params.push(...filter.exclude);
      }
      const row = db.query<QuestionRow, (string | number)[]>(`select * from questions where ${where.join(" and ")} order by random() limit 1`).get(...params);
      return row ? toQuestion(row) : null;
    },
  };
}
