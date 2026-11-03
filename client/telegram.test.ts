import { describe, expect, test } from "bun:test";
import { roomOf, signInWithTelegram, startUp, telegramLaunch } from "./telegram";

describe("the Telegram launch", () => {
  test("is read from the Telegram WebApp object", () => {
    const env = { Telegram: { WebApp: { initData: "query_id=1&hash=ab", initDataUnsafe: { start_param: "room_4821" } } }, location: { hash: "" } };

    expect(telegramLaunch(env)).toEqual({ initData: "query_id=1&hash=ab", startParam: "room_4821" });
  });

  test("is read from the tgWebAppData launch parameter in the URL hash", () => {
    const data = "query_id=1&user=%7B%7D&hash=ab";
    const env = { location: { hash: `#tgWebAppData=${encodeURIComponent(data)}&tgWebAppStartParam=room_77&tgWebAppVersion=8` } };

    expect(telegramLaunch(env)).toEqual({ initData: data, startParam: "room_77" });
  });

  test("is absent outside Telegram", () => {
    expect(telegramLaunch({ location: { hash: "#/play" } })).toBeNull();
    expect(telegramLaunch({ Telegram: { WebApp: { initData: "" } }, location: { hash: "" } })).toBeNull();
  });
});

describe("signing in from Telegram", () => {
  test("posts the launch data to the Telegram sign-in", async () => {
    const calls: [string, unknown][] = [];

    const ok = await signInWithTelegram({ initData: "abc", startParam: null }, async (path, body) => { calls.push([path, body]); });

    expect(ok).toBe(true);
    expect(calls).toEqual([["/api/auth/telegram", { initData: "abc" }]]);
  });

  test("reports a refusal instead of throwing, so the sign-in page can take over", async () => {
    const ok = await signInWithTelegram({ initData: "abc", startParam: null }, async () => { throw new Error("401"); });

    expect(ok).toBe(false);
  });
});

describe("a start parameter", () => {
  test("room_<code> names that room", () => {
    expect(roomOf("room_4821")).toBe("4821");
    expect(roomOf("room_abc234")).toBe("ABC234");
  });

  test("anything else names no room", () => {
    expect(roomOf(null)).toBeNull();
    expect(roomOf("room_")).toBeNull();
    expect(roomOf("room_../admin")).toBeNull();
    expect(roomOf("other_4821")).toBeNull();
  });
});

describe("starting up", () => {
  const launch = (startParam: string | null = null) => ({ initData: "abc", startParam });
  const run = async (opts: { launch: ReturnType<typeof launch> | null; signedIn?: boolean; pathname?: string; refuse?: boolean }) => {
    const posts: [string, unknown][] = [];
    const replaced: string[] = [];
    let loads = 0;
    let signedIn = opts.signedIn ?? false;
    await startUp({
      load: async () => { loads++; return signedIn; },
      post: async (path, body) => {
        posts.push([path, body]);
        if (opts.refuse) throw new Error("401");
        signedIn = true;
      },
      launch: opts.launch,
      pathname: opts.pathname ?? "/",
      replace: (path) => replaced.push(path),
    });
    return { posts, replaced, loads };
  };

  test("signs in with Telegram when nobody is signed in, then reloads the player", async () => {
    const r = await run({ launch: launch() });

    expect(r.posts).toEqual([["/api/auth/telegram", { initData: "abc" }]]);
    expect(r.loads).toBe(2);
  });

  test("does not post to Telegram sign-in when somebody is already signed in", async () => {
    const r = await run({ launch: launch(), signedIn: true });

    expect(r.posts).toEqual([]);
    expect(r.loads).toBe(1);
  });

  test("outside Telegram it neither posts nor redirects", async () => {
    const r = await run({ launch: null });

    expect(r.posts).toEqual([]);
    expect(r.replaced).toEqual([]);
  });

  test("a room_<code> start parameter at the home path opens joining that room", async () => {
    const r = await run({ launch: launch("room_ABC234") });

    expect(r.replaced).toEqual(["/join/ABC234"]);
  });

  test("a start parameter that is no room, or another path, does not redirect", async () => {
    expect((await run({ launch: launch("promo_1") })).replaced).toEqual([]);
    expect((await run({ launch: launch("room_ABC234"), pathname: "/play" })).replaced).toEqual([]);
  });

  test("a refused Telegram sign-in still finishes, leaving the sign-in page to take over", async () => {
    const r = await run({ launch: launch(), refuse: true });

    expect(r.posts.length).toBe(1);
    expect(r.loads).toBe(1);
  });
});
