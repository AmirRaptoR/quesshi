import type { Db } from "./db";
import type { Lang } from "./identity.store";
import type { Media, StoredQuestion } from "./questions.store";

/** How many sets one player may own. */
export const MAX_SETS_PER_PLAYER = 20;
/** How many questions one set may hold. */
export const MAX_QUESTIONS_PER_SET = 100;
/** The status of a set's questions: shared draws take only 'approved', so these never reach them. */
export const PRIVATE_STATUS = "private";

export interface QuestionSet { id: string; ownerId: string; name: string; lang: Lang; createdAt: string; questionCount: number }
/** A question to append: the set supplies its language, status and (absent) category. */
export type SetQuestionInput = Pick<StoredQuestion, "id" | "kind" | "level" | "prompt" | "payload" | "explanation"> & { media?: Media | null };

interface SetRow { id: string; owner_id: string; name: string; lang: Lang; created_at: string; question_count: number }
const toSet = (row: SetRow): QuestionSet => ({ id: row.id, ownerId: row.owner_id, name: row.name, lang: row.lang, createdAt: row.created_at, questionCount: row.question_count });
const SELECT_SET = "select question_sets.*, (select count(*) from questions where set_id = question_sets.id) as question_count from question_sets";

interface QuestionRow { id: string; kind: string; lang: string; category_id: string | null; level: number | null; prompt: string; payload: string; explanation: string | null; media: string | null }
const toQuestion = (row: QuestionRow): StoredQuestion => ({
  id: row.id, kind: row.kind, lang: row.lang, categoryId: row.category_id, level: row.level, prompt: row.prompt, payload: JSON.parse(row.payload), explanation: row.explanation,
  media: row.media === null ? null : (JSON.parse(row.media) as Media),
});

export type QuestionSetsStore = ReturnType<typeof createQuestionSetsStore>;

/** Players' private question sets and the questions inside them. */
export function createQuestionSetsStore(db: Db) {
  return {
    /** A player's sets, oldest first. */
    list(ownerId: string): QuestionSet[] {
      return db.query<SetRow, [string]>(`${SELECT_SET} where owner_id = ? order by created_at, id`).all(ownerId).map(toSet);
    },
    set(id: string): QuestionSet | null {
      const row = db.query<SetRow, [string]>(`${SELECT_SET} where id = ?`).get(id);
      return row ? toSet(row) : null;
    },
    /** Creates a set, or returns "limit" when the player already owns the maximum. */
    create(set: { id: string; ownerId: string; name: string; lang: Lang; createdAt: string }): "created" | "limit" {
      return db.transaction(() => {
        if (db.query<{ n: number }, [string]>("select count(*) as n from question_sets where owner_id = ?").get(set.ownerId)!.n >= MAX_SETS_PER_PLAYER) return "limit";
        db.run("insert into question_sets (id, owner_id, name, lang, created_at) values (?, ?, ?, ?, ?)", [set.id, set.ownerId, set.name, set.lang, set.createdAt]);
        return "created";
      })();
    },
    rename(id: string, name: string): boolean {
      return db.run("update question_sets set name = ? where id = ?", [name, id]).changes > 0;
    },
    /** Deletes a set; its questions go with it. */
    remove(id: string): boolean {
      return db.transaction(() => {
        db.run("delete from questions where set_id = ?", [id]);
        return db.run("delete from question_sets where id = ?", [id]).changes > 0;
      })();
    },
    /** A set's questions, oldest first. */
    questions(setId: string): StoredQuestion[] {
      return db.query<QuestionRow, [string]>("select * from questions where set_id = ? order by created_at, rowid").all(setId).map(toQuestion);
    },
    removeQuestion(setId: string, questionId: string): boolean {
      return db.run("delete from questions where id = ? and set_id = ?", [questionId, setId]).changes > 0;
    },
    /**
     * Appends already-validated questions in order until the set holds the maximum; the rest are
     * refused. Returns how many were added and how many refused.
     */
    append(setId: string, questions: readonly SetQuestionInput[], createdAt: string): { added: number; refused: number } {
      return db.transaction(() => {
        const set = db.query<{ lang: string }, [string]>("select lang from question_sets where id = ?").get(setId);
        if (!set) return { added: 0, refused: questions.length };
        const held = db.query<{ n: number }, [string]>("select count(*) as n from questions where set_id = ?").get(setId)!.n;
        const accepted = questions.slice(0, Math.max(0, MAX_QUESTIONS_PER_SET - held));
        for (const q of accepted) {
          db.run(
            "insert into questions (id, kind, lang, category_id, level, prompt, payload, explanation, media, status, created_at, set_id) values (?, ?, ?, null, ?, ?, ?, ?, ?, ?, ?, ?)",
            [q.id, q.kind, set.lang, q.level, q.prompt, JSON.stringify(q.payload), q.explanation, q.media ? JSON.stringify(q.media) : null, PRIVATE_STATUS, createdAt, setId],
          );
        }
        return { added: accepted.length, refused: questions.length - accepted.length };
      })();
    },
    /** One random question of the set whose kind is among those given, skipping those already used, or null. */
    draw(setId: string, filter: { kinds: readonly string[]; exclude: readonly string[] }): StoredQuestion | null {
      const where = ["set_id = ?", `kind in (${filter.kinds.map(() => "?").join(",")})`];
      const params: string[] = [setId, ...filter.kinds];
      if (filter.exclude.length) {
        where.push(`id not in (${filter.exclude.map(() => "?").join(",")})`);
        params.push(...filter.exclude);
      }
      const row = db.query<QuestionRow, string[]>(`select * from questions where ${where.join(" and ")} order by random() limit 1`).get(...params);
      return row ? toQuestion(row) : null;
    },
  };
}
