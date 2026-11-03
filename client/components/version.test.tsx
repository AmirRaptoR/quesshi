import { afterEach, describe, expect, test } from "bun:test";
import { render } from "preact-render-to-string";
import { Version } from "./version";

const before = process.env.QUESSHI_VERSION;
afterEach(() => {
  if (before === undefined) delete process.env.QUESSHI_VERSION;
  else process.env.QUESSHI_VERSION = before;
});

describe("the version line", () => {
  test("names the release the client was built as, left to right even in Persian", () => {
    process.env.QUESSHI_VERSION = "20261103.2";

    expect(render(<Version />)).toBe('<span class="faint" dir="ltr">Quesshi 20261103.2</span>');
  });

  test("says dev outside a release", () => {
    delete process.env.QUESSHI_VERSION;

    expect(render(<Version />)).toContain("Quesshi dev");
  });
});
