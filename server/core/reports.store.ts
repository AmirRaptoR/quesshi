import type { Db } from "./db";

/** Today's threshold: this many complaints take an approved question out of play until someone looks. */
const REPORTS_BEFORE_SUPPRESSED = 3;

export type ReportsStore = ReturnType<typeof createReportsStore>;

/** Players' complaints about questions. */
export function createReportsStore(db: Db) {
  return {
    /** Records a complaint from a player who was served the question; a third one sends an approved question back to pending. */
    report(questionId: string, playerId: string, reason: string, at: string): "not_served" | "already" | "reported" {
      return db.transaction(() => {
        const served = db.query<{ n: number }, [string, string]>(
          "select count(*) as n from session_questions join seats using (session_id) where question_id = ? and player_id = ?",
        ).get(questionId, playerId)!.n > 0;
        if (!served) return "not_served" as const;
        const added = db.run("insert or ignore into question_reports (question_id, player_id, reason, at) values (?, ?, ?, ?)", [questionId, playerId, reason, at]).changes > 0;
        if (!added) return "already" as const;
        db.run(
          "update questions set status = 'pending' where id = ?1 and status = 'approved' and (select count(*) from question_reports where question_id = ?1) >= ?2",
          [questionId, REPORTS_BEFORE_SUPPRESSED],
        );
        return "reported" as const;
      })();
    },
    /** Reported question ids, most recently reported first, with their complaints oldest first. */
    reported(skip: number, take: number) {
      const total = db.query<{ n: number }, []>("select count(distinct question_id) as n from question_reports").get()!.n;
      const ids = db.query<{ question_id: string }, [number, number]>(
        "select question_id from question_reports group by question_id order by max(at) desc, question_id limit ? offset ?",
      ).all(take, skip).map((row) => row.question_id);
      const reports = ids.map((id) => ({
        questionId: id,
        reports: db.query<{ playerId: string; displayName: string | null; reason: string; at: string }, [string]>(
          "select player_id as playerId, players.display_name as displayName, reason, at from question_reports left join players on players.id = player_id where question_id = ? order by at, player_id",
        ).all(id),
      }));
      return { total, reports };
    },
    /** Clears the complaints after a human looked, and puts the question back in play. False when there is no such question. */
    dismiss(questionId: string): boolean {
      return db.transaction(() => {
        db.run("delete from question_reports where question_id = ?", [questionId]);
        // A question from a player's private set keeps its status: approving it would put it into the shared draws.
        db.run("update questions set status = 'approved' where id = ? and set_id is null", [questionId]);
        return db.query("select 1 from questions where id = ?").get(questionId) !== null;
      })();
    },
  };
}
