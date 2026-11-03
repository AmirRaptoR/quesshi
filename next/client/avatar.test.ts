import { describe, expect, test } from "bun:test";
import { AVATAR_SEEDS, initial, isAvatarSeed, tint } from "./avatar";

describe("avatars", () => {
  test("the palette is today's twelve seeds, each valid", () => {
    expect(AVATAR_SEEDS).toHaveLength(12);
    expect(AVATAR_SEEDS[0]).toBe("avatar-0");
    expect(AVATAR_SEEDS.every(isAvatarSeed)).toBe(true);
  });

  test("a seed outside the palette is not valid", () => {
    expect(isAvatarSeed("avatar-12")).toBe(false);
    expect(isAvatarSeed("")).toBe(false);
  });

  test("a seed always has the same tint, and the palette offers visibly different ones", () => {
    expect(tint("avatar-3")).toBe(tint("avatar-3"));
    expect(tint("avatar-3")).toMatch(/^hsl\(\d+ 55% 62%\)$/);
    expect(new Set(AVATAR_SEEDS.map(tint)).size).toBeGreaterThan(1);
  });

  test("the initial is the name's first letter, upper-cased, or a question mark", () => {
    expect(initial("ann")).toBe("A");
    expect(initial("علی")).toBe("ع");
    expect(initial("")).toBe("?");
  });
});
