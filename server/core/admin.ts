import { createHash, randomBytes } from "node:crypto";
import type { AdminStore, Admin } from "./admin.store";
import type { Clock } from "./clock";
import en from "./i18n/en.json";
import type { Mailer } from "./mailer";
import { burnTime, hashPassword, verifyPassword } from "./passwords";

const MAX_FAILED_ATTEMPTS = 10;
const LOCK_MS = 15 * 60_000;
const RESET_MINUTES = 60;
export const ADMIN_SESSION_HOURS = 8;
const OBVIOUS = ["password", "passw0rd", "12345678", "123456789", "qwerty", "letmein", "admin", "administrator", "quesshi", "welcome", "changeme", "iloveyou"];

/** Today's password policy for administrators. */
export function passwordProblems(password: string): string[] {
  if (!password.trim()) return ["password.blank"];
  const problems: string[] = [];
  if (password.length < 10) problems.push("password.tooShort");
  if (password.length > 200) problems.push("password.tooLong");
  const folded = password.trim().toLowerCase();
  const letters = [...folded].filter((c) => /\p{L}/u.test(c)).join("");
  // A long passphrase is only too obvious when its letters are nothing but an obvious word.
  const obvious = folded.length >= 16 ? OBVIOUS.some((word) => letters === word) : OBVIOUS.some((word) => folded.includes(word));
  if (obvious) problems.push("password.tooObvious");
  return problems;
}

export type LoginResult = { ok: true; admin: Admin; mustChangePassword: boolean } | { ok: false; error: "admin.auth.invalid" } | { ok: false; error: "admin.auth.locked"; until: string };
export type PasswordResult = { ok: true } | { ok: false; error: string; problems?: string[] };

const sha256 = (value: string) => createHash("sha256").update(value).digest("hex");

/** Administrator sign-in, sessions and password changes, as today. */
export function createAdminAuth({ admins, clock, mailer, origin }: { admins: AdminStore; clock: Clock; mailer: Mailer; origin: string }) {
  const now = () => clock.now();
  const locked = (admin: Admin) => admin.lockedUntil !== null && now().getTime() < Date.parse(admin.lockedUntil);

  return {
    async login(username: string, password: string): Promise<LoginResult> {
      const admin = admins.byUsername(username);
      if (!admin || !admin.active) {
        await burnTime();
        return { ok: false, error: "admin.auth.invalid" };
      }
      if (locked(admin)) return { ok: false, error: "admin.auth.locked", until: admin.lockedUntil! };
      if (!(await verifyPassword(admin.passwordHash, password))) {
        const lockNow = admin.failedAttempts + 1 >= MAX_FAILED_ATTEMPTS;
        const until = lockNow ? new Date(now().getTime() + LOCK_MS).toISOString() : null;
        admins.recordFailure(admin.id, until);
        return until ? { ok: false, error: "admin.auth.locked", until } : { ok: false, error: "admin.auth.invalid" };
      }
      admins.recordSuccess(admin.id, now().toISOString());
      return { ok: true, admin, mustChangePassword: admin.mustChangePassword };
    },

    openSession(admin: Admin): string {
      const token = randomBytes(32).toString("base64url");
      const created = now();
      admins.createSession({ tokenHash: sha256(token), adminId: admin.id, createdAt: created.toISOString(), expiresAt: new Date(created.getTime() + ADMIN_SESSION_HOURS * 3600_000).toISOString() });
      return token;
    },
    adminFor: (token: string | undefined) => (token ? admins.sessionAdmin(sha256(token), now().toISOString()) : null),
    closeSession: (token: string | undefined) => { if (token) admins.deleteSession(sha256(token)); },

    /** Mails a one-hour reset link; an unknown or inactive account gets nothing and the same answer. */
    async requestReset(handle: string): Promise<void> {
      const admin = admins.byUsername(handle) ?? admins.byEmail(handle);
      if (!admin || !admin.active) return;
      const secret = randomBytes(32).toString("base64url");
      admins.saveResetToken({ secretHash: sha256(secret), adminId: admin.id, issuedAt: now().toISOString() });
      // Administration is an English-language surface, as today.
      const link = `${origin}/admin/reset?token=${secret}`;
      await mailer.send({
        to: admin.email,
        subject: en["email.reset.subject"],
        text: [en["email.reset.title"], en["email.reset.intro"].replace("{0}", admin.username), link, en["email.reset.note"].replace("{0}", String(RESET_MINUTES))].join("\n\n"),
      });
    },

    async reset(secret: string, password: string): Promise<PasswordResult> {
      const token = admins.resetToken(sha256(secret));
      if (!token) return { ok: false, error: "admin.auth.linkInvalid" };
      if (token.used) return { ok: false, error: "admin.auth.linkUsed" };
      if (now().getTime() > Date.parse(token.issuedAt) + RESET_MINUTES * 60_000) return { ok: false, error: "admin.auth.linkExpired" };
      // Judge the password before spending the link: a typo should not cost the admin their link.
      const problems = passwordProblems(password);
      if (problems.length) return { ok: false, error: "admin.auth.weak", problems };
      admins.useResetToken(sha256(secret));
      admins.setPassword(token.adminId, await hashPassword(password));
      return { ok: true };
    },

    async changePassword(admin: Admin, current: string, next: string): Promise<PasswordResult> {
      if (!(await verifyPassword(admin.passwordHash, current))) return { ok: false, error: "admin.auth.wrongCurrent" };
      const problems = passwordProblems(next);
      if (problems.length) return { ok: false, error: "admin.auth.weak", problems };
      admins.setPassword(admin.id, await hashPassword(next));
      return { ok: true };
    },

    async create(account: { username: string; email: string; password: string; mustChangePassword: boolean }): Promise<string> {
      const id = crypto.randomUUID();
      admins.create({ id, username: account.username, email: account.email, passwordHash: await hashPassword(account.password), mustChangePassword: account.mustChangePassword, createdAt: now().toISOString() });
      return id;
    },
  };
}

export type AdminAuth = ReturnType<typeof createAdminAuth>;
