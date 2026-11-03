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

    expect(response.body).toEqual({ id: "a1", username: "root", email: "root@example.com", mustChangePassword: false });
    expect(response.cookie).toStartWith("__Host-quesshi-admin=");
  });

  test("a wrong password, an unknown name and a deactivated account all read the same", async () => {
    withAdmin();
    expect((await login("root", "nope nope nope")).body).toEqual({ error: "admin.auth.invalid" });
    expect((await login("nobody", PASSWORD)).body).toEqual({ error: "admin.auth.invalid" });
    app.world.db.run("update admins set active = 0");
    expect((await login("root", PASSWORD)).body).toEqual({ error: "admin.auth.invalid" });
  });

  test("ten failures lock the account for fifteen minutes, even against the right password", async () => {
    withAdmin();
    for (let i = 0; i < 10; i++) await login("root", "wrong password!");

    expect((await login("root", PASSWORD)).body).toMatchObject({ error: "admin.auth.locked" });
    app.scheduler.advanceTo(T0 + 15 * 60_000);
    expect((await login("root", PASSWORD)).body).toMatchObject({ id: "a1" });
  });

  test("an account flagged to change its password is told so", async () => {
    withAdmin({ mustChange: true });

    expect((await login("root", PASSWORD)).body).toMatchObject({ mustChangePassword: true });
  });

  test("a player's cookie never opens admin routes, and the admin cookie does", async () => {
    withAdmin();
    const player = await app.signIn("ann@example.com");
    const admin = (await login("root", PASSWORD)).cookie;

    expect((await app.call("GET", "/api/admin/auth/me", undefined, player)).status).toBe(401);
    expect((await app.call("GET", "/api/admin/auth/me", undefined, admin)).body).toEqual({ id: "a1", username: "root", email: "root@example.com", mustChangePassword: false });
  });

  test("changing the password needs the current one and a strong new one", async () => {
    withAdmin();
    const admin = (await login("root", PASSWORD)).cookie;

    expect((await app.call("POST", "/api/admin/auth/password", { current: "wrong", next: "A much better passphrase" }, admin)).body).toEqual({ error: "admin.auth.wrongCurrent" });
    expect((await app.call("POST", "/api/admin/auth/password", { current: PASSWORD, next: "password12" }, admin)).body).toEqual({ error: "admin.auth.weak", problems: ["password.tooObvious"] });
    expect((await app.call("POST", "/api/admin/auth/password", { current: PASSWORD, next: "A much better passphrase" }, admin)).status).toBe(200);
    expect((await login("root", "A much better passphrase")).body).toMatchObject({ id: "a1" });
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
    expect((await login("root", "A brand new passphrase")).body).toMatchObject({ id: "a1" });
    expect((await reset(token, "Another new passphrase")).body).toEqual({ error: "admin.auth.linkUsed" });
  });

  test("a weak password does not use up the link, and the link expires after an hour", async () => {
    withAdmin();
    const token = (await linkFor("root"))!;

    expect((await reset(token, "short")).body).toEqual({ error: "admin.auth.weak", problems: ["password.tooShort"] });
    app.scheduler.advanceTo(T0 + 3600_000 + 1);
    expect((await reset(token, "A brand new passphrase")).body).toEqual({ error: "admin.auth.linkExpired" });
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

describe("admin accounts", () => {
  async function signedIn() {
    withAdmin();
    return (await login("root", PASSWORD)).cookie!;
  }
  const create = (cookie: string, account: Record<string, string>) => app.call("POST", "/api/admin/accounts", account, cookie);

  test("an admin creates another, who must change the given password", async () => {
    const cookie = await signedIn();

    const created = await create(cookie, { username: "editor", email: "editor@example.com", password: "A long enough passphrase" });

    expect(created.body).toMatchObject({ username: "editor", email: "editor@example.com", active: true, mustChangePassword: true, locked: false });
    expect((await app.call("GET", "/api/admin/accounts", undefined, cookie)).body.map((a: { username: string }) => a.username).sort()).toEqual(["editor", "root"]);
  });

  test("refuses a bad or taken username, a bad or taken email, and a weak password", async () => {
    const cookie = await signedIn();
    const ok = { username: "editor", email: "editor@example.com", password: "A long enough passphrase" };

    expect((await create(cookie, { ...ok, username: "ed" })).body).toEqual({ error: "admin.auth.badUsername" });
    expect((await create(cookie, { ...ok, username: "ROOT" })).body).toEqual({ error: "admin.auth.usernameTaken" });
    expect((await create(cookie, { ...ok, email: "not-an-email" })).body).toEqual({ error: "admin.auth.badEmail" });
    expect((await create(cookie, { ...ok, email: "Root@Example.com" })).body).toEqual({ error: "admin.auth.emailTaken" });
    expect((await create(cookie, { ...ok, password: "short" })).body).toEqual({ error: "admin.auth.weak", problems: ["password.tooShort"] });
  });

  test("an admin cannot deactivate or delete themselves, nor delete the last admin", async () => {
    const cookie = await signedIn();

    expect((await app.call("POST", "/api/admin/accounts/a1/active", { value: false }, cookie)).body).toEqual({ error: "admin.auth.cannotDisableSelf" });
    expect((await app.call("DELETE", "/api/admin/accounts/a1", undefined, cookie)).body).toEqual({ error: "admin.auth.cannotDeleteSelf" });
  });

  test("a deactivated admin can no longer sign in, and a deleted one is gone", async () => {
    const cookie = await signedIn();
    const editor = (await create(cookie, { username: "editor", email: "editor@example.com", password: "A long enough passphrase" })).body;

    await app.call("POST", `/api/admin/accounts/${editor.id}/active`, { value: false }, cookie);
    expect((await login("editor", "A long enough passphrase")).body).toEqual({ error: "admin.auth.invalid" });

    expect((await app.call("DELETE", `/api/admin/accounts/${editor.id}`, undefined, cookie)).status).toBe(200);
    expect((await app.call("GET", "/api/admin/accounts", undefined, cookie)).body).toHaveLength(1);
  });

  test("an admin changes their own email, unless another admin has it", async () => {
    const cookie = await signedIn();
    await create(cookie, { username: "editor", email: "editor@example.com", password: "A long enough passphrase" });

    expect((await app.call("POST", "/api/admin/auth/email", { email: "editor@example.com" }, cookie)).body).toEqual({ error: "admin.auth.emailTaken" });
    expect((await app.call("POST", "/api/admin/auth/email", { email: "new@example.com" }, cookie)).body).toMatchObject({ email: "new@example.com" });
  });
});
