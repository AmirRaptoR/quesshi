import { describe, expect, test } from "bun:test";
import { destination } from "./signin";

describe("after signing in", () => {
  test("goes back to a path on this site, and never to another host", () => {
    expect(destination("/join/ABCDEF")).toBe("/join/ABCDEF");
    expect(destination("//evil.example.com")).toBe("/play");
    expect(destination("https://evil.example.com")).toBe("/play");
    expect(destination(undefined)).toBe("/play");
  });
});
