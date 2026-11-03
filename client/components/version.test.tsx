import { describe, expect, test } from "bun:test";
import { render } from "preact-render-to-string";
import { version } from "../../package.json";
import { Version } from "./version";

describe("the version line", () => {
  test("names the release from package.json, left to right even in Persian", () => {
    expect(render(<Version />)).toBe(`<span class="faint" dir="ltr">Quesshi ${version}</span>`);
  });
});
