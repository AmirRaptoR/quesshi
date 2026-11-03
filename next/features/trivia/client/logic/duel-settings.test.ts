import { describe, expect, test } from "bun:test";
import { translator } from "../../../../client/i18n";
import { draftLink, readDraft, readStored, safeReturn, settingsLine, summary, toSettings } from "./duel-settings";

const t = translator("en", {
  "home.settingsSeparator": " · ", "home.settingsQuestions": "{0} questions", "home.settingsMixed": "Mixed", "home.settingsAllTopics": "All topics",
  "home.settingsTopics": "{0} topics", "level.2": "Easy", "level.3": "Medium", "level.4": "Hard",
});
const saved = { lang: "en", questions: 30, categoryIds: ["geo", "science"], levels: [2, 3] };

describe("the settings line", () => {
  test("no level, or every level, reads as mixed", () => {
    expect(summary({ ...saved, levels: [] }).mixed).toBe(true);
    expect(summary({ ...saved, levels: [1, 2, 3, 4, 5] }).mixed).toBe(true);
  });

  test("one level reads as its name, a narrowed choice by its ends, unsorted or with holes", () => {
    expect(settingsLine({ ...saved, levels: [3] }, t)).toBe("30 questions · Medium · 2 topics");
    expect(settingsLine({ ...saved, levels: [4, 2] }, t)).toBe("30 questions · Easy–Hard · 2 topics");
    expect(settingsLine({ ...saved, levels: [0, 3, 9] }, t)).toBe("30 questions · Medium · 2 topics");
  });

  test("no topic picked means all topics", () => {
    expect(settingsLine({ ...saved, categoryIds: [], levels: [] }, t)).toBe("30 questions · Mixed · All topics");
  });
});

describe("the remembered settings", () => {
  test("read back what was stored, and fall back on anything unreadable", () => {
    expect(readStored(JSON.stringify(saved), "fa")).toEqual(saved);
    for (const raw of [null, "{", "[]", JSON.stringify({ ...saved, lang: "klingon" }), JSON.stringify({ ...saved, questions: "ten" })]) {
      expect(readStored(raw, "fa")).toEqual({ lang: "fa", questions: 10, categoryIds: [], levels: [] });
    }
  });
});

describe("the questions sheet's draft", () => {
  test("a bare arrival opens on the last saved settings and returns to the play hub", () => {
    expect(readDraft("", saved)).toEqual({ lang: "en", questions: 30, levels: { low: 2, high: 3 }, categoryIds: ["geo", "science"], returnTo: "/play", lobby: null });
  });

  test("what the address carries wins over what was saved", () => {
    const draft = readDraft("?lang=nl&n=50&lo=1&hi=4&t=history", saved);
    expect([draft.lang, draft.questions, draft.levels, draft.categoryIds]).toEqual(["nl", 50, { low: 1, high: 4 }, ["history"]]);
  });

  test("a draft and the lobby it was opened from survive a trip through a link", () => {
    const draft = { ...readDraft("", saved), categoryIds: [], returnTo: "/lobby/ABCDEF", lobby: { id: "m1", format: "live" } };
    expect(readDraft(draftLink("/play/questions", draft).split("?")[1]!, saved)).toEqual(draft);
  });

  test("unreadable values fall back to the saved ones; half a range is no range; a crossed one is uncrossed", () => {
    expect(readDraft("?lang=klingon&n=-5", saved)).toMatchObject({ lang: "en", questions: 30 });
    expect(readDraft("?lo=1", saved).levels).toEqual({ low: 2, high: 3 });
    const crossed = readDraft("?lo=4&hi=2", saved).levels;
    expect(crossed.low).toBeLessThanOrEqual(crossed.high);
  });

  test("the whole ramp goes back as an empty level list", () => {
    expect(toSettings(readDraft("?lo=1&hi=5", saved))).toEqual({ lang: "en", questions: 30, categoryIds: ["geo", "science"], levels: [] });
  });

  test("only a path inside the app is a place to return to", () => {
    expect(safeReturn("/lobby/ABCDEF")).toBe("/lobby/ABCDEF");
    for (const value of ["//evil.example/phish", "/\\evil.example", "https://evil.example", "javascript:alert(1)", "play", "", null]) expect(safeReturn(value)).toBe("/play");
  });
});
