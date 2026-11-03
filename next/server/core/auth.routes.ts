import { Elysia } from "elysia";
import type { createAuth } from "./auth";
import { SESSION_DAYS } from "./auth";
import type { Lang, Player } from "./identity.store";

const COOKIE = "__Host-quesshi";
const LANGS: readonly Lang[] = ["fa", "en", "nl"];
const EMAIL = /^[^@\s]+@[^@\s]+\.[^@\s]+$/;

type Auth = ReturnType<typeof createAuth>;

/** Reads the sign-in token from the request's cookie. */
export function sessionToken(request: Request): string | undefined {
  const cookies = request.headers.get("cookie") ?? "";
  for (const part of cookies.split(";")) {
    const [name, ...value] = part.trim().split("=");
    if (name === COOKIE) return value.join("=");
  }
  return undefined;
}

const sessionCookie = (token: string, maxAge: number) => `${COOKIE}=${token}; Path=/; HttpOnly; Secure; SameSite=Lax; Max-Age=${maxAge}`;
const lang = (value: unknown): Lang => (LANGS.includes(value as Lang) ? (value as Lang) : "fa");
export const playerJson = (player: Player) => ({
  id: player.id, email: player.email, displayName: player.displayName, avatarSeed: player.avatarSeed, lang: player.lang, guest: player.guest,
});

export function authRoutes(auth: Auth, newId: () => string) {
  return new Elysia()
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
      set.headers["set-cookie"] = sessionCookie(auth.openSession(result.player), SESSION_DAYS * 24 * 3600);
      return playerJson(result.player);
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
    });
}
