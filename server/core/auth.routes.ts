import { Elysia } from "elysia";
import type { createAuth } from "./auth";
import { SESSION_DAYS } from "./auth";
import type { Clock } from "./clock";
import type { Core } from "./feature";
import { verifyInitData } from "./telegram";
import type { IdentityStore, Lang, Player } from "./identity.store";

const COOKIE = "__Host-quesshi";
const LANGS: readonly Lang[] = ["fa", "en", "nl"];
const EMAIL = /^[^@\s]+@[^@\s]+\.[^@\s]+$/;
/** Today's twelve avatars. */
const AVATARS = new Set(Array.from({ length: 12 }, (_, i) => `avatar-${i}`));

type Auth = ReturnType<typeof createAuth>;

/** Reads the sign-in token from the request's cookie. */
export function sessionToken(request: Request): string | undefined {
  return tokenFromCookies(request.headers.get("cookie"));
}

export function tokenFromCookies(header: string | null): string | undefined {
  const cookies = header ?? "";
  for (const part of cookies.split(";")) {
    const [name, ...value] = part.trim().split("=");
    if (name === COOKIE) return value.join("=");
  }
  return undefined;
}

export const sessionCookie = (token: string, maxAge: number) => `${COOKIE}=${token}; Path=/; HttpOnly; Secure; SameSite=Lax; Max-Age=${maxAge}`;
const lang = (value: unknown): Lang => (LANGS.includes(value as Lang) ? (value as Lang) : "fa");
export const playerJson = (player: Player) => ({
  id: player.id, email: player.email, displayName: player.displayName, avatarSeed: player.avatarSeed, lang: player.lang, guest: player.guest,
});

export function authRoutes(auth: Auth, identity: IdentityStore, google: Core["google"], telegramBotToken: string | undefined, clock: Clock, newId: () => string) {
  const signedIn = (player: Player, set: { headers: Record<string, unknown> }) => {
    set.headers["set-cookie"] = sessionCookie(auth.openSession(player), SESSION_DAYS * 24 * 3600);
    return playerJson(player);
  };
  return new Elysia()
    .get("/api/auth/config", () => ({ googleEnabled: Boolean(google), googleClientId: google?.clientId ?? null, telegramEnabled: Boolean(telegramBotToken) }))
    .post("/api/auth/google", async ({ body, set }) => {
      if (!google) {
        set.status = 400;
        return { error: "google_disabled" };
      }
      const { idToken, lang: requested } = (body ?? {}) as { idToken?: unknown; lang?: unknown };
      const verified = typeof idToken === "string" ? await google.verify(idToken) : null;
      if (!verified) {
        set.status = 400;
        return { error: "invalid_token" };
      }
      const player = auth.signInWithGoogle(verified.email, verified.name, lang(requested), newId);
      if (player === "banned") {
        set.status = 400;
        return { error: "banned" };
      }
      return signedIn(player, set);
    })
    .post("/api/auth/telegram", ({ body, set }) => {
      if (!telegramBotToken) {
        set.status = 400;
        return { error: "telegram_disabled" };
      }
      const { initData } = (body ?? {}) as { initData?: unknown };
      const user = typeof initData === "string" ? verifyInitData(initData, telegramBotToken, clock.now()) : null;
      if (!user) {
        set.status = 401;
        return { error: "invalid_init_data" };
      }
      const player = auth.signInWithTelegram(user.id, user.name, user.lang ?? "fa", newId);
      if (player === "banned") {
        set.status = 400;
        return { error: "banned" };
      }
      return signedIn(player, set);
    })
    .post("/api/auth/code", async ({ body, set }) => {
      const { email, lang: requested } = (body ?? {}) as { email?: unknown; lang?: unknown };
      if (typeof email !== "string" || !EMAIL.test(email.trim())) {
        set.status = 400;
        return { error: "invalid_email" };
      }
      await auth.requestCode(email, lang(requested));
      // Always "sent", so the answer never tells whether the address has an account.
      return { sent: true };
    })
    .post("/api/auth/code/verify", ({ body, set }) => {
      const { email, code, lang: requested } = (body ?? {}) as { email?: unknown; code?: unknown; lang?: unknown };
      if (typeof email !== "string" || typeof code !== "string") {
        set.status = 400;
        return { error: "invalid_request" };
      }
      const result = auth.verifyCode(email, code, lang(requested), newId);
      if (!result.ok) {
        set.status = 400;
        return { error: result.error };
      }
      return signedIn(result.player, set);
    })
    .post("/api/auth/sign-out", ({ request, set }) => {
      auth.closeSession(sessionToken(request));
      set.headers["set-cookie"] = sessionCookie("", 0);
      return { signedOut: true };
    })
    .get("/api/me", ({ request, set }) => {
      const player = auth.playerFor(sessionToken(request));
      if (!player) {
        set.status = 401;
        return { error: "signed_out" };
      }
      return playerJson(player);
    })
    .post("/api/me/upgrade", ({ request, body, set }) => {
      const player = auth.playerFor(sessionToken(request));
      if (!player) {
        set.status = 401;
        return { error: "signed_out" };
      }
      const { email, code } = (body ?? {}) as { email?: unknown; code?: unknown };
      if (typeof email !== "string" || typeof code !== "string") {
        set.status = 400;
        return { error: "invalid_request" };
      }
      const result = auth.upgradeGuest(player, email, code);
      if (!result.ok) {
        set.status = 400;
        return { error: result.error };
      }
      return signedIn(result.player, set);
    })
    .put("/api/me", ({ request, body, set }) => {
      const player = auth.playerFor(sessionToken(request));
      if (!player) {
        set.status = 401;
        return { error: "signed_out" };
      }
      const { displayName, avatarSeed, lang: requested } = (body ?? {}) as { displayName?: unknown; avatarSeed?: unknown; lang?: unknown };
      const name = typeof displayName === "string" ? displayName.trim() : "";
      if (name.length < 2 || name.length > 24) {
        set.status = 400;
        return { error: "name_length" };
      }
      // A missing avatar leaves it as it is.
      if (avatarSeed !== undefined && avatarSeed !== null && !AVATARS.has(avatarSeed as string)) {
        set.status = 400;
        return { error: "bad_avatar" };
      }
      identity.updateProfile(player.id, { displayName: name, avatarSeed: (avatarSeed as string | null | undefined) ?? null, lang: lang(requested) });
      return playerJson(identity.player(player.id)!);
    });
}
