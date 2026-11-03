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
    playerByTelegram(telegramId: string): Player | null {
      const row = db.query<PlayerRow, [string]>("select * from players where telegram_id = ?").get(telegramId);
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
    createTelegramPlayer(player: { id: string; telegramId: string; displayName: string; lang: Lang; createdAt: string }): void {
      db.run(
        "insert into players (id, email, display_name, avatar_seed, lang, guest, created_at, telegram_id) values (?, null, ?, ?, ?, 0, ?, ?)",
        [player.id, player.displayName, player.id, player.lang, player.createdAt, player.telegramId],
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
    /** Members (never guests) newest first, matching name or email when a search is given. */
    members(search: string | undefined, skip: number, take: number): { items: (Player & { createdAt: string })[]; total: number } {
      const clause = search ? "where guest = 0 and (instr(lower(display_name), lower(?1)) > 0 or instr(lower(email), lower(?1)) > 0)" : "where guest = 0 and ?1 is null";
      const total = db.query<{ n: number }, [string | null]>(`select count(*) as n from players ${clause}`).get(search ?? null)!.n;
      const rows = db.query<PlayerRow & { created_at: string }, [string | null, number, number]>(
        `select * from players ${clause} order by created_at desc, id limit ?2 offset ?3`,
      ).all(search ?? null, take, skip);
      return { items: rows.map((row) => ({ ...toPlayer(row), createdAt: row.created_at })), total };
    },
    /** Bans or lifts a ban; a ban also ends every session. False when there is no such player. */
    setBanned(id: string, banned: boolean, at: string): boolean {
      return db.transaction(() => {
        const changed = db.run("update players set banned_at = ? where id = ?", [banned ? at : null, id]).changes > 0;
        if (changed && banned) db.run("delete from auth_sessions where player_id = ?", [id]);
        return changed;
      })();
    },
    deleteSession(tokenHash: string): void {
      db.run("delete from auth_sessions where token_hash = ?", [tokenHash]);
    },
  };
}
