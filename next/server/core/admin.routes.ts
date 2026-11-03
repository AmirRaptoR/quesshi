import { Elysia } from "elysia";
import { ADMIN_SESSION_HOURS, type AdminAuth } from "./admin";
import type { Admin } from "./admin.store";

const COOKIE = "__Host-quesshi-admin";
const cookie = (token: string, maxAge: number) => `${COOKIE}=${token}; Path=/; HttpOnly; Secure; SameSite=Lax; Max-Age=${maxAge}`;

/** The admin's sign-in token from a cookie header; players and admins use different cookies. */
export function adminToken(header: string | null): string | undefined {
  for (const part of (header ?? "").split(";")) {
    const [name, ...value] = part.trim().split("=");
    if (name === COOKIE) return value.join("=");
  }
  return undefined;
}

export const adminJson = (admin: Admin) => ({ id: admin.id, username: admin.username, email: admin.email });

export function adminAuthRoutes(auth: AdminAuth) {
  const str = (value: unknown) => (typeof value === "string" ? value : "");
  return new Elysia({ prefix: "/api/admin/auth" })
    .post("/login", async ({ body, set }) => {
      const { username, password } = (body ?? {}) as Record<string, unknown>;
      const result = await auth.login(str(username), str(password));
      if (!result.ok) {
        set.status = result.error === "locked" ? 423 : 401;
        return result.error === "locked" ? { error: "locked", until: result.until } : { error: result.error };
      }
      set.headers["set-cookie"] = cookie(auth.openSession(result.admin), ADMIN_SESSION_HOURS * 3600);
      return { status: result.mustChangePassword ? "must_change_password" : "ok", admin: adminJson(result.admin) };
    })
    .post("/forgot", async ({ body }) => {
      await auth.requestReset(str((body as Record<string, unknown> | null)?.handle));
      return { sent: true };
    })
    .post("/reset", async ({ body, set }) => {
      const { token, password } = (body ?? {}) as Record<string, unknown>;
      const result = await auth.reset(str(token), str(password));
      if (result.ok) return { reset: true };
      set.status = 400;
      return result.problems ? { error: result.error, problems: result.problems } : { error: result.error };
    })
    .resolve(({ request }) => ({ admin: auth.adminFor(adminToken(request.headers.get("cookie"))) }))
    .onBeforeHandle(({ admin, set }) => {
      if (!admin) {
        set.status = 401;
        return { error: "signed_out" };
      }
    })
    .get("/me", ({ admin }) => adminJson(admin!))
    .post("/password", async ({ admin, body, set }) => {
      const { current, next } = (body ?? {}) as Record<string, unknown>;
      const result = await auth.changePassword(admin!, str(current), str(next));
      if (result.ok) return { changed: true };
      set.status = 400;
      return result.problems ? { error: result.error, problems: result.problems } : { error: result.error };
    })
    .post("/sign-out", ({ request, set }) => {
      auth.closeSession(adminToken(request.headers.get("cookie")));
      set.headers["set-cookie"] = cookie("", 0);
      return { signedOut: true };
    });
}
