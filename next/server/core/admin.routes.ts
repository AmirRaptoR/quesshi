import { Elysia } from "elysia";
import { ADMIN_SESSION_HOURS, type AdminAuth } from "./admin";
import type { Admin, AdminStore } from "./admin.store";
import { passwordProblems } from "./admin";

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

export const adminJson = (admin: Admin) => ({ id: admin.id, username: admin.username, email: admin.email, mustChangePassword: admin.mustChangePassword });

const EMAIL = /^[^@\s]+@[^@\s]+\.[^@\s]+$/;

/** A base for admin-only routes: resolves the signed-in admin and refuses everyone else. */
export function adminOnly(auth: AdminAuth, prefix: string) {
  return new Elysia({ prefix })
    .resolve(({ request }) => ({ admin: auth.adminFor(adminToken(request.headers.get("cookie"))) as Admin }))
    .onBeforeHandle(({ admin, set }) => {
      if (!admin) {
        set.status = 401;
        return { error: "signed_out" };
      }
    });
}

/** Today's account management: any admin manages the others, never themselves, and never the last one. */
export function adminAccountRoutes(auth: AdminAuth, admins: AdminStore, now: () => Date) {
  const describe = (a: ReturnType<AdminStore["all"]>[number]) => ({
    id: a.id, username: a.username, email: a.email, active: a.active, mustChangePassword: a.mustChangePassword,
    createdAt: a.createdAt, lastLoginAt: a.lastLoginAt, locked: a.lockedUntil !== null && now().getTime() < Date.parse(a.lockedUntil),
  });
  const find = (id: string) => admins.all().find((a) => a.id === id);
  const bad = (set: { status?: number | string }, error: string, extra: Record<string, unknown> = {}) => {
    set.status = 400;
    return { error, ...extra };
  };
  return adminOnly(auth, "/api/admin/accounts")
    .get("/", () => admins.all().map(describe))
    .post("/", async ({ body, set }) => {
      const { username, email, password } = (body ?? {}) as Record<string, unknown>;
      const name = typeof username === "string" ? username.trim() : "";
      const address = typeof email === "string" ? email.trim() : "";
      const secret = typeof password === "string" ? password : "";
      if (name.length < 3 || name.length > 32) return bad(set, "admin.auth.badUsername");
      if (admins.byUsername(name)) return bad(set, "admin.auth.usernameTaken");
      // The email is the only way back in after a forgotten password, so it must be unique.
      if (!EMAIL.test(address)) return bad(set, "admin.auth.badEmail");
      if (admins.byEmail(address)) return bad(set, "admin.auth.emailTaken");
      const problems = passwordProblems(secret);
      if (problems.length) return bad(set, "admin.auth.weak", { problems });
      const id = await auth.create({ username: name, email: address, password: secret, mustChangePassword: true });
      return describe(find(id)!);
    })
    .post("/:id/active", ({ admin, params, body, set }) => {
      if (params.id === admin.id) return bad(set, "admin.auth.cannotDisableSelf");
      if (!find(params.id)) return bad(set, "not_found");
      admins.setActive(params.id, (body as { value?: unknown } | null)?.value === true);
      return describe(find(params.id)!);
    })
    .delete("/:id", ({ admin, params, set }) => {
      if (params.id === admin.id) return bad(set, "admin.auth.cannotDeleteSelf");
      // Never leave the installation with no way in.
      if (admins.count() <= 1) return bad(set, "admin.auth.lastAdmin");
      admins.remove(params.id);
      return { deleted: true };
    });
}

export function adminAuthRoutes(auth: AdminAuth, admins: AdminStore) {
  const str = (value: unknown) => (typeof value === "string" ? value : "");
  return new Elysia({ prefix: "/api/admin/auth" })
    .post("/login", async ({ body, set }) => {
      const { username, password } = (body ?? {}) as Record<string, unknown>;
      const result = await auth.login(str(username), str(password));
      if (!result.ok) {
        set.status = result.error === "admin.auth.locked" ? 429 : 401;
        return result.error === "admin.auth.locked" ? { error: result.error, lockedUntil: result.until } : { error: result.error };
      }
      set.headers["set-cookie"] = cookie(auth.openSession(result.admin), ADMIN_SESSION_HOURS * 3600);
      return adminJson(result.admin);
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
    .post("/email", ({ admin, body, set }) => {
      const email = (body as { email?: unknown } | null)?.email;
      if (typeof email !== "string" || !EMAIL.test(email.trim())) {
        set.status = 400;
        return { error: "admin.auth.badEmail" };
      }
      const other = admins.byEmail(email);
      if (other && other.id !== admin!.id) {
        set.status = 400;
        return { error: "admin.auth.emailTaken" };
      }
      admins.setEmail(admin!.id, email);
      return adminJson(admins.byId(admin!.id)!);
    })
    .post("/sign-out", ({ request, set }) => {
      auth.closeSession(adminToken(request.headers.get("cookie")));
      set.headers["set-cookie"] = cookie("", 0);
      return { signedOut: true };
    });
}
