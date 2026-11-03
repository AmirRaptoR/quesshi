import type { Db } from "./db";

export interface Player {
  id: string;
  email: string | null;
  displayName: string;
  avatarSeed: string;
  lang: Lang;
  guest: boolean;
  banned: boolean;
}
export type Lang = "fa" | "en" | "nl";

interface PlayerRow { id: string; email: string | null; display_name: string; avatar_seed: string; lang: Lang; guest: number; banned_at: string | null }
const toPlayer = (row: PlayerRow): Player => ({
  id: row.id, email: row.email, displayName: row.display_name, avatarSeed: row.avatar_seed, lang: row.lang, guest: row.guest === 1, banned: row.banned_at !== null,
});

export type IdentityStore = ReturnType<typeof createIdentityStore>;

/** Players, sign-in codes and sign-in sessions. */
export function createIdentityStore(db: Db) {
  return {
    playerByEmail(email: string): Player | null {
      const row = db.query<PlayerRow, [string]>("select * from players where email = ?").get(email);
      return row ? toPlayer(row) : null;
    },
    player(id: string): Player | null {
      const row = db.query<PlayerRow, [string]>("select * from players where id = ?").get(id);
      return row ? toPlayer(row) : null;
    },
    createPlayer(player: { id: string; email: string | null; displayName: string; lang: Lang; guest: boolean; createdAt: string }): void {
      db.run(
        "insert into players (id, email, display_name, avatar_seed, lang, guest, created_at) values (?, ?, ?, ?, ?, ?, ?)",
        [player.id, player.email, player.displayName, player.id, player.lang, player.guest ? 1 : 0, player.createdAt],
      );
    },
    claimEmail(id: string, email: string): void {
      db.run("update players set email = ?, guest = 0 where id = ?", [email, id]);
    },
    updateProfile(id: string, profile: { displayName: string; avatarSeed: string | null; lang: Lang }): void {
      db.run("update players set display_name = ?, avatar_seed = coalesce(?, avatar_seed), lang = ? where id = ?", [profile.displayName, profile.avatarSeed, profile.lang, id]);
    },
    saveCode(email: string, codeHash: string, issuedAt: string): void {
      db.run(
        "insert into login_codes (email, code_hash, issued_at, attempts) values (?, ?, ?, 0) on conflict (email) do update set code_hash = excluded.code_hash, issued_at = excluded.issued_at, attempts = 0",
        [email, codeHash, issuedAt],
      );
    },
    code(email: string): { codeHash: string; issuedAt: string; attempts: number } | null {
      return db.query<{ codeHash: string; issuedAt: string; attempts: number }, [string]>(
        "select code_hash as codeHash, issued_at as issuedAt, attempts from login_codes where email = ?",
      ).get(email);
    },
    countAttempt(email: string): void {
      db.run("update login_codes set attempts = attempts + 1 where email = ?", [email]);
    },
    deleteCode(email: string): void {
      db.run("delete from login_codes where email = ?", [email]);
    },
    createSession(session: { tokenHash: string; playerId: string; createdAt: string; expiresAt: string }): void {
      db.run("insert into auth_sessions (token_hash, player_id, created_at, expires_at) values (?, ?, ?, ?)", [session.tokenHash, session.playerId, session.createdAt, session.expiresAt]);
    },
    sessionPlayer(tokenHash: string, now: string): Player | null {
      const row = db.query<PlayerRow, [string, string]>(
        "select players.* from auth_sessions join players on players.id = auth_sessions.player_id where token_hash = ? and expires_at > ?",
      ).get(tokenHash, now);
      return row ? toPlayer(row) : null;
    },
    deleteSession(tokenHash: string): void {
      db.run("delete from auth_sessions where token_hash = ?", [tokenHash]);
    },
  };
}
