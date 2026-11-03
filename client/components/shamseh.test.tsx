import { describe, expect, test } from "bun:test";
import { render } from "preact-render-to-string";
import { Shamseh } from "./shamseh";

const rays = (html: string) => [...html.matchAll(/shamseh__ray--(\w+)/g)].map((m) => m[1]);

describe("the shamseh", () => {
  test("has one ray per question each, saffron for right, pomegranate for wrong, dashed for not yet", () => {
    const html = render(<Shamseh mine={[true, false, null]} theirs={[true]} slots={3} label="Progress" />);

    expect(rays(html)).toEqual(["right", "wrong", "idle", "right", "idle", "idle"]);
  });

  test("hides the other side's answers when asked", () => {
    expect(rays(render(<Shamseh mine={[true]} theirs={[true]} slots={2} hideTheirs label="P" />))).toEqual(["right", "idle", "hidden", "hidden"]);
  });

  test("past twenty questions it draws two rings instead of rays", () => {
    const html = render(<Shamseh mine={[true]} theirs={[]} slots={21} label="P" />);

    expect(rays(html)).toEqual([]);
    expect(html).toContain("shamseh__rings");
  });
});
