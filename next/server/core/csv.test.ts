import { describe, expect, test } from "bun:test";
import { parseCsv } from "./csv";

describe("CSV", () => {
  test("reads quoted commas, quoted line breaks and doubled quotes", () => {
    expect(parseCsv('a,b\r\n"x, y","line\none"\n"say ""hi""",\n')).toEqual([["a", "b"], ["x, y", "line\none"], ['say "hi"', ""]]);
  });

  test("drops a byte order mark and blank lines", () => {
    expect(parseCsv("﻿a,b\n\n1,2")).toEqual([["a", "b"], ["1", "2"]]);
  });

  test("marks a record whose quote never closes as malformed", () => {
    expect(parseCsv('a,b\n"open,2')).toEqual([["a", "b"], null]);
  });
});
