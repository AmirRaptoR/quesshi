import { describe, expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import path from "node:path";

const design = readFileSync(path.join(import.meta.dir, "..", "docs", "design.md"), "utf8").replace(/\s+/g, " ");

describe("design doc", () => {
  test("lists private question sets as replacing packs in the later list", () => {
    expect(design).toContain('private question sets (which replace "packs")');
  });
});
