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

export type QuestionsStore = ReturnType<typeof createQuestionsStore>;

/** The question bank and its categories. */
export function createQuestionsStore(db: Db) {
  return {
    addCategory(category: { id: string; nameFa: string; nameEn: string; nameNl: string; icon: string; color: string }): void {
      db.run("insert into categories (id, name_fa, name_en, name_nl, icon, color) values (?, ?, ?, ?, ?, ?)", [category.id, category.nameFa, category.nameEn, category.nameNl, category.icon, category.color]);
    },
    add(question: StoredQuestion & { status: string; createdAt: string }): void {
      db.run(
        "insert into questions (id, kind, lang, category_id, level, prompt, payload, explanation, status, created_at) values (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
        [question.id, question.kind, question.lang, question.categoryId, question.level, question.prompt, JSON.stringify(question.payload), question.explanation, question.status, question.createdAt],
      );
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
