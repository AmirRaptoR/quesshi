import { afterEach, describe, expect, test } from "bun:test";
import { tempWorld } from "../../tests/support/temp";
import { createApp } from "../app";
import type { Clock } from "./clock";

const ORIGIN = "https://quesshi.example.com";
let world: ReturnType<typeof tempWorld>;
afterEach(() => world.cleanup());

function setup(google?: { verify: (token: string) => Promise<{ email: string; name: string | null } | null> }) {
  world = tempWorld();
  let now = Date.parse("2026-11-03T12:00:00Z");
  const clock: Clock = { now: () => new Date(now) };
  const mail: { to: string; text: string }[] = [];
  const { server } = createApp({ db: world.db, clock, schedule: () => () => {}, random: Math.random, secret: "test-secret", origin: ORIGIN, mailer: { send: async (message) => { mail.push(message); } }, ...(google ? { google: { clientId: "client-1", ...google } } : {}) }, []);
  const call = (method: string, path: string, body?: unknown, cookie?: string, origin = ORIGIN) =>
    server.handle(new Request(`${ORIGIN}${path}`, {
      method,
      headers: { "content-type": "application/json", origin, ...(cookie ? { cookie } : {}) },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    }));
  const lastCode = () => mail.at(-1)!.text.match(/\b(\d{6})\b/)![1]!;
  const requestCode = (email: string) => call("POST", "/api/auth/code", { email, lang: "en" });
  const verify = (email: string, code: string) => call("POST", "/api/auth/code/verify", { email, code, lang: "en" });
  const signIn = async (email: string) => {
    await requestCode(email);
    const response = await verify(email, lastCode());
    return response.headers.get("set-cookie")!.split(";")[0]!;
  };
  return { call, mail, lastCode, requestCode, verify, signIn, advance: (ms: number) => { now += ms; } };
}

describe("sign-in with an emailed code", () => {
  test("mails a six-digit code and always answers that it was sent", async () => {
    const { requestCode, mail } = setup();

    const response = await requestCode("Ann@Example.com");

    expect(await response.json()).toEqual({ sent: true });
    expect(mail).toHaveLength(1);
    expect(mail[0]!.to).toBe("ann@example.com");
    expect(mail[0]!.text).toMatch(/\b\d{6}\b/);
  });

  test("the right code signs in a new player named after their address, and later the same player", async () => {
    const { signIn, call } = setup();

    const first = await (await call("GET", "/api/me", undefined, await signIn("ann.lee@example.com"))).json();
    const again = await (await call("GET", "/api/me", undefined, await signIn("ann.lee@example.com"))).json();

    expect(first).toMatchObject({ displayName: "ann.lee", email: "ann.lee@example.com", lang: "en", guest: false });
    expect(again.id).toBe(first.id);
  });

  test("a wrong code fails, and after five attempts even the right one is refused", async () => {
    const { requestCode, verify, lastCode } = setup();
    await requestCode("ann@example.com");
    const right = lastCode();
    const wrong = right === "000000" ? "111111" : "000000";

    expect(await (await verify("ann@example.com", wrong)).json()).toEqual({ error: "wrong" });
    for (let i = 0; i < 4; i++) await verify("ann@example.com", wrong);

    expect(await (await verify("ann@example.com", right)).json()).toEqual({ error: "too_many_attempts" });
  });

  test("a code expires after ten minutes and works only once", async () => {
    const { requestCode, verify, lastCode, advance } = setup();
    await requestCode("ann@example.com");
    const code = lastCode();
    advance(10 * 60_000 + 1);

    expect(await (await verify("ann@example.com", code)).json()).toEqual({ error: "expired" });

    await requestCode("ann@example.com");
    const fresh = lastCode();
    expect((await verify("ann@example.com", fresh)).status).toBe(200);
    expect(await (await verify("ann@example.com", fresh)).json()).toEqual({ error: "expired" });
  });

  test("a banned player's right code reads as wrong", async () => {
    const { signIn, requestCode, verify, lastCode } = setup();
    await signIn("ann@example.com");
    world.db.run("update players set banned_at = '2026-11-03T12:00:00Z' where email = 'ann@example.com'");

    await requestCode("ann@example.com");

    expect(await (await verify("ann@example.com", lastCode())).json()).toEqual({ error: "wrong" });
  });
});

describe("the sign-in session", () => {
  test("signing out ends it", async () => {
    const { signIn, call } = setup();
    const cookie = await signIn("ann@example.com");

    await call("POST", "/api/auth/sign-out", undefined, cookie);

    expect((await call("GET", "/api/me", undefined, cookie)).status).toBe(401);
  });

  test("it lasts thirty days", async () => {
    const { signIn, call, advance } = setup();
    const cookie = await signIn("ann@example.com");

    advance(30 * 24 * 3600_000 - 1);
    expect((await call("GET", "/api/me", undefined, cookie)).status).toBe(200);
    advance(2);
    expect((await call("GET", "/api/me", undefined, cookie)).status).toBe(401);
  });

  test("the cookie cannot be read by scripts and is sent only to this site", async () => {
    const { requestCode, verify, lastCode } = setup();
    await requestCode("ann@example.com");

    const cookie = (await verify("ann@example.com", lastCode())).headers.get("set-cookie")!;

    expect(cookie).toContain("HttpOnly");
    expect(cookie).toContain("Secure");
    expect(cookie).toContain("SameSite=Lax");
  });

  test("a change requested from another site is refused", async () => {
    const { call } = setup();

    const response = await call("POST", "/api/auth/code", { email: "ann@example.com", lang: "en" }, undefined, "https://evil.example.net");

    expect(response.status).toBe(403);
  });
});

describe("profile", () => {
  test("a player changes their name, avatar and language", async () => {
    const { signIn, call } = setup();
    const cookie = await signIn("ann@example.com");

    const response = await call("PUT", "/api/me", { displayName: "  Annie  ", avatarSeed: "avatar-3", lang: "nl" }, cookie);

    expect(await response.json()).toMatchObject({ displayName: "Annie", avatarSeed: "avatar-3", lang: "nl" });
  });

  test("refuses a name outside 2 to 24 characters and an unknown avatar", async () => {
    const { signIn, call } = setup();
    const cookie = await signIn("ann@example.com");

    expect(await (await call("PUT", "/api/me", { displayName: "A", lang: "en" }, cookie)).json()).toEqual({ error: "name_length" });
    expect(await (await call("PUT", "/api/me", { displayName: "x".repeat(25), lang: "en" }, cookie)).json()).toEqual({ error: "name_length" });
    expect(await (await call("PUT", "/api/me", { displayName: "Annie", avatarSeed: "avatar-12", lang: "en" }, cookie)).json()).toEqual({ error: "bad_avatar" });
  });
});

describe("sign-in with Google", () => {
  const google = { verify: async (token: string) => (token === "good" ? { email: "Bo@Example.com", name: "Bo Diddley" } : null) };

  test("a verified Google account signs in the player with that email, named as on Google", async () => {
    const { call } = setup(google);

    const response = await call("POST", "/api/auth/google", { idToken: "good", lang: "en" });

    expect(await response.json()).toMatchObject({ email: "bo@example.com", displayName: "Bo Diddley" });
    expect(response.headers.get("set-cookie")).toContain("HttpOnly");
  });

  test("refuses a token Google does not vouch for, and is off without a client id", async () => {
    expect(await (await setup(google).call("POST", "/api/auth/google", { idToken: "forged" })).json()).toEqual({ error: "invalid_token" });
    world.cleanup();
    expect(await (await setup().call("POST", "/api/auth/google", { idToken: "good" })).json()).toEqual({ error: "google_disabled" });
  });

  test("a banned player cannot sign in with Google", async () => {
    const { call } = setup(google);
    await call("POST", "/api/auth/google", { idToken: "good" });
    world.db.run("update players set banned_at = '2026-11-03T12:00:00Z'");

    expect(await (await call("POST", "/api/auth/google", { idToken: "good" })).json()).toEqual({ error: "banned" });
  });
});

describe("guest upgrade", () => {
  async function guest() {
    const ctx = setup();
    const id = crypto.randomUUID();
    world.db.run("insert into players (id, email, display_name, avatar_seed, lang, guest, created_at) values (?, null, 'Dina', ?, 'en', 1, '2026-11-03T12:00:00Z')", [id, id]);
    world.db.run("insert into auth_sessions (token_hash, player_id, created_at, expires_at) values (?, ?, '2026-11-03T12:00:00Z', '2026-12-03T12:00:00Z')",
      [new Bun.CryptoHasher("sha256").update("guest-token").digest("hex"), id]);
    return { ...ctx, id, cookie: "__Host-quesshi=guest-token" };
  }

  test("a guest proves an email with a code and keeps their player as a full member", async () => {
    const { call, requestCode, lastCode, id, cookie } = await guest();
    await requestCode("dina@example.com");

    const upgraded = await call("POST", "/api/me/upgrade", { email: "dina@example.com", code: lastCode() }, cookie);

    expect(await upgraded.json()).toMatchObject({ id, email: "dina@example.com", displayName: "Dina", guest: false });
    expect(upgraded.headers.get("set-cookie")).toContain("HttpOnly");
  });

  test("refuses an address that already has an account, and a wrong code", async () => {
    const { call, requestCode, lastCode, signIn, cookie } = await guest();
    await signIn("taken@example.com");
    await requestCode("taken@example.com");

    expect(await (await call("POST", "/api/me/upgrade", { email: "taken@example.com", code: lastCode() }, cookie)).json()).toEqual({ error: "address_taken" });
    await requestCode("dina@example.com");
    expect(await (await call("POST", "/api/me/upgrade", { email: "dina@example.com", code: "999999" === lastCode() ? "000000" : "999999" }, cookie)).json()).toEqual({ error: "wrong" });
  });

  test("a signed-in member cannot use it to change their email", async () => {
    const { call, signIn, requestCode, lastCode } = setup();
    const cookie = await signIn("ann@example.com");
    await requestCode("other@example.com");

    expect(await (await call("POST", "/api/me/upgrade", { email: "other@example.com", code: lastCode() }, cookie)).json()).toEqual({ error: "not_a_guest" });
  });
});
