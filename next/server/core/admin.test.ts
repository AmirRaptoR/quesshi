import { afterEach, describe, expect, test } from "bun:test";
import { ORIGIN, T0, testApp } from "../../tests/support/app";

let app: ReturnType<typeof testApp>;
afterEach(() => app.cleanup());

/** Today's ASP.NET Identity hash of "Correct horse battery 9", made by the real PasswordHasher. */
const LEGACY_HASH = "AQAAAAIAAYagAAAAEIMhStRpzHj0KJcjTHOQ2gtXiOOLWmQwTF2A0150Y2lsYL0eWoa3SV5fIBgGcjM+kQ==";
const PASSWORD = "Correct horse battery 9";

function withAdmin(options: { active?: boolean; mustChange?: boolean } = {}) {
  app = testApp([]);
  app.world.db.run(
    "insert into admins (id, username, email, password_hash, active, must_change_password, failed_attempts, created_at) values ('a1', 'root', 'root@example.com', ?, ?, ?, 0, '2026-11-03T12:00:00Z')",
    [LEGACY_HASH, options.active === false ? 0 : 1, options.mustChange ? 1 : 0],
  );
  return app;
}
const login = (username: string, password: string) => app.call("POST", "/api/admin/auth/login", { username, password });

describe("admin sign-in", () => {
  test("today's password hashes keep working, and sign-in sets the admin cookie", async () => {
    withAdmin();

    const response = await login("root", PASSWORD);

    expect(response.body).toEqual({ status: "ok", admin: { id: "a1", username: "root", email: "root@example.com" } });
    expect(response.cookie).toStartWith("__Host-quesshi-admin=");
  });

  test("a wrong password, an unknown name and a deactivated account all read the same", async () => {
    withAdmin();
    expect((await login("root", "nope nope nope")).body).toEqual({ error: "invalid_credentials" });
    expect((await login("nobody", PASSWORD)).body).toEqual({ error: "invalid_credentials" });
    app.world.db.run("update admins set active = 0");
    expect((await login("root", PASSWORD)).body).toEqual({ error: "invalid_credentials" });
  });

  test("ten failures lock the account for fifteen minutes, even against the right password", async () => {
    withAdmin();
    for (let i = 0; i < 10; i++) await login("root", "wrong password!");

    expect((await login("root", PASSWORD)).body).toMatchObject({ error: "locked" });
    app.scheduler.advanceTo(T0 + 15 * 60_000);
    expect((await login("root", PASSWORD)).body).toMatchObject({ status: "ok" });
  });

  test("an account flagged to change its password is told so", async () => {
    withAdmin({ mustChange: true });

    expect((await login("root", PASSWORD)).body).toMatchObject({ status: "must_change_password" });
  });

  test("a player's cookie never opens admin routes, and the admin cookie does", async () => {
    withAdmin();
    const player = await app.signIn("ann@example.com");
    const admin = (await login("root", PASSWORD)).cookie;

    expect((await app.call("GET", "/api/admin/auth/me", undefined, player)).status).toBe(401);
    expect((await app.call("GET", "/api/admin/auth/me", undefined, admin)).body).toEqual({ id: "a1", username: "root", email: "root@example.com" });
  });

  test("changing the password needs the current one and a strong new one", async () => {
    withAdmin();
    const admin = (await login("root", PASSWORD)).cookie;

    expect((await app.call("POST", "/api/admin/auth/password", { current: "wrong", next: "A much better passphrase" }, admin)).body).toEqual({ error: "wrong_current_password" });
    expect((await app.call("POST", "/api/admin/auth/password", { current: PASSWORD, next: "password12" }, admin)).body).toEqual({ error: "weak_password", problems: ["password.tooObvious"] });
    expect((await app.call("POST", "/api/admin/auth/password", { current: PASSWORD, next: "A much better passphrase" }, admin)).status).toBe(200);
    expect((await login("root", "A much better passphrase")).body).toMatchObject({ status: "ok" });
  });
});

describe("admin password reset", () => {
  async function linkFor(handle: string) {
    await app.call("POST", "/api/admin/auth/forgot", { handle });
    return app.mail.at(-1)?.text.match(/token=([A-Za-z0-9_-]+)/)?.[1];
  }
  const reset = (token: string, password: string) => app.call("POST", "/api/admin/auth/reset", { token, password });

  test("a reset link by username or email sets a new password once", async () => {
    withAdmin({ mustChange: true });
    const token = (await linkFor("root@example.com"))!;

    expect((await reset(token, "A brand new passphrase")).status).toBe(200);
    expect((await login("root", "A brand new passphrase")).body).toMatchObject({ status: "ok" });
    expect((await reset(token, "Another new passphrase")).body).toEqual({ error: "used_token" });
  });

  test("a weak password does not use up the link, and the link expires after an hour", async () => {
    withAdmin();
    const token = (await linkFor("root"))!;

    expect((await reset(token, "short")).body).toEqual({ error: "weak_password", problems: ["password.tooShort"] });
    app.scheduler.advanceTo(T0 + 3600_000 + 1);
    expect((await reset(token, "A brand new passphrase")).body).toEqual({ error: "expired_token" });
  });

  test("an unknown account gets the same answer and no mail", async () => {
    withAdmin();
    const sent = app.mail.length;

    expect((await app.call("POST", "/api/admin/auth/forgot", { handle: "nobody" })).body).toEqual({ sent: true });
    expect(app.mail.length).toBe(sent);
  });
});

describe("first administrator", () => {
  test("is created from the configuration when there is none, and must change a generated password", async () => {
    app = testApp([], { bootstrapAdmin: { username: "admin", email: "admin@example.com", password: null } });

    expect(app.world.db.query("select username, must_change_password from admins").all()).toEqual([{ username: "admin", must_change_password: 1 }]);
    expect(ORIGIN).toBeDefined();
  });
});
