import { describe, expect, test } from "bun:test";
import { buildPrompt } from "./set-prompt";

const input = { count: 30, lang: "en", topic: "Space travel", difficulty: "hard" } as const;

describe("the AI prompt for a question set", () => {
  test("carries the chosen count, language, topic and difficulty", () => {
    const prompt = buildPrompt(input);
    expect(prompt).toContain("Write 30 multiple-choice quiz questions in English about: Space travel.");
    expect(prompt).toContain("Difficulty: hard");
    expect(prompt).not.toMatch(/\{(count|language|topic|difficulty)\}/);
  });

  test("is written in the set's language: Persian, English and Dutch each have a translation", () => {
    const fa = buildPrompt({ ...input, lang: "fa" });
    const nl = buildPrompt({ ...input, lang: "nl" });
    expect(fa).toContain("۴ گزینه");
    expect(fa).toContain("به زبان فارسی");
    expect(nl).toContain("Schrijf 30 meerkeuzevragen voor een quiz in het Nederlands over: Space travel.");
    expect(nl).toContain("Moeilijkheid: moeilijk");
    expect(new Set([buildPrompt(input), fa, nl]).size).toBe(3);
    for (const prompt of [fa, nl]) expect(prompt).toContain('"correctindex": 0');
  });

  test("leaves a topic that looks like a placeholder as typed", () => {
    expect(buildPrompt({ ...input, topic: "{count} and {language}" })).toContain("about: {count} and {language}.");
  });
});
