import { describe, expect, test } from "bun:test";
import { roomOf, signInWithTelegram, telegramLaunch } from "./telegram";

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
