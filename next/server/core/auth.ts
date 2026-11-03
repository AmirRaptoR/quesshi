import { createHash, createHmac, randomBytes, randomInt, timingSafeEqual } from "node:crypto";
import type { Clock } from "./clock";
import type { IdentityStore, Lang, Player } from "./identity.store";
import { signInCodeMail, type Mailer } from "./mailer";

const CODE_MINUTES = 10;
const MAX_ATTEMPTS = 5;
export const SESSION_DAYS = 30;

export type VerifyResult = { ok: true; player: Player } | { ok: false; error: "wrong" | "expired" | "too_many_attempts" };

interface Deps { identity: IdentityStore; clock: Clock; mailer: Mailer; secret: string }

/** Passwordless sign-in with an emailed code, and the sessions it opens. */
export function createAuth({ identity, clock, mailer, secret }: Deps) {
  const hashCode = (email: string, code: string) => createHmac("sha256", secret).update(`${email}:${code}`).digest("hex");
  const hashToken = (token: string) => createHash("sha256").update(token).digest("hex");
  const now = () => clock.now();
  /** Checks a code and uses it up when right; returns the failure, or null. */
  const checkCode = (email: string, code: string): "wrong" | "expired" | "too_many_attempts" | null => {
    const challenge = identity.code(email);
    // An unknown address reads as an expired code, so nobody can probe which addresses exist.
    if (!challenge) return "expired";
    if (challenge.attempts >= MAX_ATTEMPTS) return "too_many_attempts";
    if (now().getTime() > Date.parse(challenge.issuedAt) + CODE_MINUTES * 60_000) return "expired";
    identity.countAttempt(email);
    if (!timingSafeEqual(Buffer.from(challenge.codeHash, "hex"), Buffer.from(hashCode(email, code), "hex"))) return "wrong";
    identity.deleteCode(email);
    return null;
  };
  const playerFor = (email: string, displayName: string, lang: Lang, newId: () => string): Player => {
    const existing = identity.playerByEmail(email);
    if (existing) return existing;
    identity.createPlayer({ id: newId(), email, displayName, lang, guest: false, createdAt: now().toISOString() });
    return identity.playerByEmail(email)!;
  };

  return {
    async requestCode(rawEmail: string, lang: Lang): Promise<void> {
      const email = normalize(rawEmail);
      const code = String(randomInt(0, 1_000_000)).padStart(6, "0");
      identity.saveCode(email, hashCode(email, code), now().toISOString());
      await mailer.send(signInCodeMail(email, code, lang, CODE_MINUTES));
    },

    verifyCode(rawEmail: string, code: string, lang: Lang, newId: () => string): VerifyResult {
      const email = normalize(rawEmail);
      const checked = checkCode(email, code);
      if (checked) return { ok: false, error: checked };
      const player = playerFor(email, email.split("@")[0] || "player", lang, newId);
      // A ban reads as a wrong code: never confirm that the address has an account.
      return player.banned ? { ok: false, error: "wrong" } : { ok: true, player };
    },

    /** Signs in the player with a Google-verified address, creating them under their Google name. */
    signInWithGoogle(rawEmail: string, name: string | null, lang: Lang, newId: () => string): Player | "banned" {
      const email = normalize(rawEmail);
      const player = playerFor(email, name?.trim() || email.split("@")[0] || "player", lang, newId);
      return player.banned ? "banned" : player;
    },

    /** A guest proves an address with a code and becomes a full member, keeping their player. */
    upgradeGuest(guest: Player, rawEmail: string, code: string): { ok: true; player: Player } | { ok: false; error: string } {
      // Only a guest has no address to replace; a member must not turn this into "change my email".
      if (!guest.guest) return { ok: false, error: "not_a_guest" };
      const email = normalize(rawEmail);
      const checked = checkCode(email, code);
      if (checked) return { ok: false, error: checked };
      if (identity.playerByEmail(email)) return { ok: false, error: "address_taken" };
      identity.claimEmail(guest.id, email);
      return { ok: true, player: identity.player(guest.id)! };
    },

    /** Creates a guest: a player with a name and no address, who can join games by code. */
    createGuest(name: string, lang: Lang, newId: () => string): Player {
      const id = newId();
      identity.createPlayer({ id, email: null, displayName: name, lang, guest: true, createdAt: now().toISOString() });
      return identity.player(id)!;
    },

    /** Opens a session for the player and returns the token for the cookie. */
    openSession(player: Player): string {
      const token = randomBytes(32).toString("base64url");
      const created = now();
      identity.createSession({
        tokenHash: hashToken(token),
        playerId: player.id,
        createdAt: created.toISOString(),
        expiresAt: new Date(created.getTime() + SESSION_DAYS * 24 * 3600_000).toISOString(),
      });
      return token;
    },

    playerFor(token: string | undefined): Player | null {
      return token ? identity.sessionPlayer(hashToken(token), now().toISOString()) : null;
    },

    closeSession(token: string | undefined): void {
      if (token) identity.deleteSession(hashToken(token));
    },
  };
}

const normalize = (email: string) => email.trim().toLowerCase();
