import type { Db } from "./db";

export interface Admin {
  id: string;
  username: string;
  email: string;
  passwordHash: string;
  active: boolean;
  mustChangePassword: boolean;
  failedAttempts: number;
  lockedUntil: string | null;
}

interface AdminRow { id: string; username: string; email: string; password_hash: string; active: number; must_change_password: number; failed_attempts: number; locked_until: string | null }
const toAdmin = (row: AdminRow): Admin => ({
  id: row.id, username: row.username, email: row.email, passwordHash: row.password_hash, active: row.active === 1,
  mustChangePassword: row.must_change_password === 1, failedAttempts: row.failed_attempts, lockedUntil: row.locked_until,
});

export type AdminStore = ReturnType<typeof createAdminStore>;

/** Administrator accounts, their sessions and password reset links. */
export function createAdminStore(db: Db) {
  const one = (sql: string, ...params: string[]) => {
    const row = db.query<AdminRow, string[]>(sql).get(...params);
    return row ? toAdmin(row) : null;
  };
  return {
    count: () => db.query<{ n: number }, []>("select count(*) as n from admins").get()!.n,
    byId: (id: string) => one("select * from admins where id = ?", id),
    byUsername: (username: string) => one("select * from admins where username = ?", username.trim()),
    byEmail: (email: string) => one("select * from admins where email = ?", email.trim()),
    create(admin: { id: string; username: string; email: string; passwordHash: string; mustChangePassword: boolean; createdAt: string }): void {
      db.run(
        "insert into admins (id, username, email, password_hash, must_change_password, created_at) values (?, ?, ?, ?, ?, ?)",
        [admin.id, admin.username.trim(), admin.email.trim().toLowerCase(), admin.passwordHash, admin.mustChangePassword ? 1 : 0, admin.createdAt],
      );
    },
    recordFailure(id: string, lockedUntil: string | null): void {
      db.run("update admins set failed_attempts = failed_attempts + 1, locked_until = coalesce(?, locked_until) where id = ?", [lockedUntil, id]);
    },
    recordSuccess(id: string, at: string): void {
      db.run("update admins set failed_attempts = 0, locked_until = null, last_login_at = ? where id = ?", [at, id]);
    },
    setPassword(id: string, passwordHash: string): void {
      db.run("update admins set password_hash = ?, must_change_password = 0, failed_attempts = 0, locked_until = null where id = ?", [passwordHash, id]);
    },
    createSession(session: { tokenHash: string; adminId: string; createdAt: string; expiresAt: string }): void {
      db.run("insert into admin_sessions (token_hash, admin_id, created_at, expires_at) values (?, ?, ?, ?)", [session.tokenHash, session.adminId, session.createdAt, session.expiresAt]);
    },
    sessionAdmin(tokenHash: string, now: string): Admin | null {
      return one("select admins.* from admin_sessions join admins on admins.id = admin_sessions.admin_id where token_hash = ? and expires_at > ? and admins.active = 1", tokenHash, now);
    },
    deleteSession(tokenHash: string): void {
      db.run("delete from admin_sessions where token_hash = ?", [tokenHash]);
    },
    saveResetToken(token: { secretHash: string; adminId: string; issuedAt: string }): void {
      db.run("insert into admin_reset_tokens (secret_hash, admin_id, issued_at) values (?, ?, ?)", [token.secretHash, token.adminId, token.issuedAt]);
    },
    resetToken(secretHash: string): { adminId: string; issuedAt: string; used: boolean } | null {
      const row = db.query<{ adminId: string; issuedAt: string; used: number }, [string]>(
        "select admin_id as adminId, issued_at as issuedAt, used from admin_reset_tokens where secret_hash = ?",
      ).get(secretHash);
      return row ? { adminId: row.adminId, issuedAt: row.issuedAt, used: row.used === 1 } : null;
    },
    useResetToken(secretHash: string): void {
      db.run("update admin_reset_tokens set used = 1 where secret_hash = ?", [secretHash]);
    },
  };
}
