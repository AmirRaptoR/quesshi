import type { Db } from "./db";

export type SessionsStore = ReturnType<typeof createSessionsStore>;

/** Sessions, their seats and every answer given in them. */
export function createSessionsStore(db: Db) {
  return {
    create(session: { id: string; code: string; format: string; createdAt: string; players: string[] }): void {
      db.transaction(() => {
        db.run("insert into sessions (id, code, format, status, created_at) values (?, ?, ?, 'running', ?)", [session.id, session.code, session.format, session.createdAt]);
        for (const player of session.players) db.run("insert into seats (session_id, player_id) values (?, ?)", [session.id, player]);
      })();
    },
    /** A null value records a miss. */
    recordAnswer(answer: { sessionId: string; playerId: string; slot: number; value: unknown; credit: number; points: number; answeredAt: string | null }): void {
      db.run(
        "insert into answers (session_id, player_id, slot, value, credit, points, answered_at) values (?, ?, ?, ?, ?, ?, ?)",
        [answer.sessionId, answer.playerId, answer.slot, answer.value === null ? null : JSON.stringify(answer.value), answer.credit, answer.points, answer.answeredAt],
      );
    },
    finish(ending: { sessionId: string; endedAt: string; reason: string; result: unknown }): void {
      db.run("update sessions set status = 'ended', ended_at = ?, ended_reason = ?, result = ? where id = ?", [ending.endedAt, ending.reason, JSON.stringify(ending.result), ending.sessionId]);
    },
  };
}
