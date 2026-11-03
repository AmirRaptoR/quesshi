import { describe, expect, test } from "bun:test";
import { soundsOn, storedSounds } from "./sounds";

describe("the sounds preference", () => {
  test("a key never written means on", () => expect(soundsOn(null)).toBe(true));
  test("an explicit zero is off", () => expect(soundsOn("0")).toBe(false));
  test("an explicit one is on", () => expect(soundsOn("1")).toBe(true));
  test("storing then reading round-trips", () => {
    expect(storedSounds(true)).toBe("1");
    expect(storedSounds(false)).toBe("0");
    expect(soundsOn(storedSounds(false))).toBe(false);
  });
});
