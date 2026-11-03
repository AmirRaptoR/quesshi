import type { Db } from "./db";

/** The admin dashboard's counts. */
export function createDashboardStore(db: Db) {
  const count = (sql: string) => db.query<{ n: number }, []>(sql).get()!.n;
  return {
    counts() {
      const byStatus = Object.fromEntries(db.query<{ status: string; n: number }, []>("select status, count(*) as n from questions group by status").all().map((row) => [row.status, row.n]));
      return {
        players: count("select count(*) as n from players where guest = 0"),
        questions: {
          total: count("select count(*) as n from questions"),
          approved: byStatus.approved ?? 0, pending: byStatus.pending ?? 0, rejected: byStatus.rejected ?? 0,
        },
        sessionsPlayed: count("select count(*) as n from sessions where status = 'ended' and ended_reason not in ('lobby_expired', 'owner_cancelled')"),
      };
    },
  };
}
