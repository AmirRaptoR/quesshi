import { describe, expect, test } from "bun:test";
import { guestReturn, usableName } from "./join";

const room = { id: "r1", code: "ABC234" };
const lobby = "/voting/lobby/ABC234";

describe("a guest reopening a join link", () => {
  test("goes back to their own lobby", () => {
    expect(guestReturn(true, lobby, room, lobby)).toBe(lobby);
  });

  test("goes back to their own game once it has started", () => {
    expect(guestReturn(true, "/voting/r1", room, lobby)).toBe("/voting/r1");
  });

  test("opening a different room's code is not redirected", () => {
    expect(guestReturn(true, "/voting/r2", room, lobby)).toBeNull();
    expect(guestReturn(true, "/voting/lobby/XYZ789", room, lobby)).toBeNull();
  });

  test("a guest with nothing remembered is not redirected", () => {
    expect(guestReturn(true, null, room, lobby)).toBeNull();
  });

  test("a signed-in member or a visitor is never redirected, whatever is remembered", () => {
    expect(guestReturn(false, lobby, room, lobby)).toBeNull();
  });
});

describe("a player's name", () => {
  test("is two to twenty-four characters once trimmed, as the server counts", () => {
    expect(usableName("A")).toBe(false);
    expect(usableName("")).toBe(false);
    expect(usableName("Al")).toBe(true);
    expect(usableName("x".repeat(24))).toBe(true);
    expect(usableName("x".repeat(25))).toBe(false);
    expect(usableName("  A  ")).toBe(false);
    expect(usableName(` ${"x".repeat(24)} `)).toBe(true);
  });
});
