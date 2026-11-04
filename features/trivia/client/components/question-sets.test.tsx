import { describe, expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import path from "node:path";
import { render } from "preact-render-to-string";
import { translator, type Lang } from "../../../../client/i18n";
import { canCreateSets, clampCount, copyText, MAX_SETS, reasonKey } from "../logic/question-sets";
import { ImportReport, PromptPanel, SetActions, SetList, SetQuestions } from "./question-sets";

const table = (lang: Lang) => JSON.parse(readFileSync(path.join(import.meta.dir, `../../../../client/public/i18n/${lang}.json`), "utf8")) as Record<string, string>;
const en = translator("en", table("en"));
const fa = translator("fa", table("fa"));
const sets = [{ id: "s1", name: "Space", lang: "en" as const, createdAt: "2026-11-03T10:00:00Z", questionCount: 12 }];

describe("the question sets list", () => {
  test("offers the create button to a member", () => {
    expect(render(<SetList t={en} sets={sets} canCreate />)).toContain('href="/sets/new"');
  });

  test("offers no create button to a guest, and says why", () => {
    const html = render(<SetList t={en} sets={[]} canCreate={canCreateSets({ guest: true })} />);
    expect(html).not.toContain("/sets/new");
    expect(html).toContain(en.t("sets.guest"));
    expect(canCreateSets(null)).toBe(false);
  });

  test("lists each set with its name and question count", () => {
    const html = render(<SetList t={fa} sets={sets} canCreate />);
    expect(html).toContain('href="/sets/s1"');
    expect(html).toContain("Space");
    expect(html).toContain("۱۲ پرسش");
  });
});

describe("copying the prompt", () => {
  test("puts the exact prompt text on the clipboard", async () => {
    const written: string[] = [];
    expect(await copyText("the prompt", { writeText: async (text) => { written.push(text); } })).toBe(true);
    expect(written).toEqual(["the prompt"]);
  });

  test("says so when the browser refuses or has no clipboard", async () => {
    expect(await copyText("x", { writeText: async () => { throw new Error("denied"); } })).toBe(false);
    expect(await copyText("x", undefined)).toBe(false);
  });

  test("the panel shows the paste instruction, the prompt and a Copy button", () => {
    const html = render(<PromptPanel t={en} prompt="Write 20 questions" copied={false} onCopy={() => {}} text="" onText={() => {}} onFile={() => {}} busy={false} onImport={() => {}} />);
    expect(html).toContain("Paste this into any AI chat, then paste its answer below.");
    expect(html).toContain("Write 20 questions");
    expect(html).toContain(">Copy<");
  });
});

describe("an import's outcome", () => {
  test("shows the accepted count and each rejected row with its reason", () => {
    const html = render(<ImportReport t={en} result={{ accepted: 7, rejected: [{ row: 3, reason: "bad_choices" }, { row: 9, reason: "set_full" }, { row: 11, reason: "brand_new_code" }] }} />);
    expect(html).toContain("7 questions added.");
    expect(html).toContain("Row 3: Four different, non-empty options are needed.");
    expect(html).toContain("Row 9: The set is full (100 questions).");
    expect(html).toContain("Row 11: This row is not valid.");
  });

  test("shows no rejection list when every row went in", () => {
    expect(render(<ImportReport t={fa} result={{ accepted: 20, rejected: [] }} />)).not.toContain("<li");
  });

  test("falls back to a generic reason for a code without text", () => {
    expect(reasonKey("bad_prompt", (key) => key in table("en"))).toBe("sets.reason.bad_prompt");
    expect(reasonKey("nope", (key) => key in table("en"))).toBe("sets.reason.other");
  });
});

describe("a set's questions", () => {
  const question = { id: "q1", prompt: "Which planet is red?", level: 1, payload: { options: ["Venus", "Mars", "Earth", "Moon"], correctIndex: 1 } };

  test("lists each question with its correct answer and a delete button", () => {
    const html = render(<SetQuestions t={en} questions={[question]} onDelete={() => {}} />);
    expect(html).toContain("Which planet is red?");
    expect(html).toContain("Mars");
    expect(html).toContain('aria-label="Delete question"');
  });
});

describe("renaming and deleting a set", () => {
  const noop = () => {};
  const handlers = { onStartRename: noop, onRenameInput: noop, onRename: noop, onCancelRename: noop, onAskDelete: noop, onDelete: noop, onCancelDelete: noop };
  const base = { t: en, renaming: null, confirming: false, ...handlers };
  const buttonsOf = (tree: any): any[] => {
    const found: any[] = [];
    const walk = (node: any) => {
      if (node?.type === "button") found.push(node);
      [node?.props?.children].flat(5).forEach((child) => child && typeof child === "object" && walk(child));
    };
    walk(tree);
    return found;
  };

  test("offers Rename and Delete, and the delete confirmation only once asked", () => {
    const html = render(<SetActions {...base} />);
    expect(html).toContain(">Rename<");
    expect(html).toContain(">Delete set<");
    expect(html).not.toContain(en.t("sets.deleteConfirm"));
    expect(render(<SetActions {...base} confirming />)).toContain(en.t("sets.deleteConfirm"));
  });

  test("renaming shows the name in an editable field with Save", () => {
    const html = render(<SetActions {...base} renaming="Space" />);
    expect(html).toContain('value="Space"');
    expect(html).toContain(">Save<");
  });

  test("Save is disabled for a blank name", () => {
    expect(render(<SetActions {...base} renaming="  " />)).toMatch(/<button[^>]*disabled[^>]*>Save</);
  });

  test("Rename starts a rename, Save submits it, and the confirmed Delete deletes the set", () => {
    const calls: string[] = [];
    const on = { ...base, onStartRename: () => calls.push("start"), onAskDelete: () => calls.push("ask"), onRename: () => calls.push("rename"), onDelete: () => calls.push("delete") };
    buttonsOf(SetActions(on)).forEach((b) => b.props.onClick());
    buttonsOf(SetActions({ ...on, renaming: "Space" })).filter((b) => b.props.disabled === false).forEach((b) => b.props.onClick());
    buttonsOf(SetActions({ ...on, confirming: true })).filter((b) => b.props.class.includes("danger")).forEach((b) => b.props.onClick());
    expect(calls).toEqual(["start", "ask", "rename", "delete"]);
  });
});

describe("the count field and the translations", () => {
  test("snaps a count into 10 to 50 and defaults a blank one to 20", () => {
    expect([clampCount("3"), clampCount("10"), clampCount("50"), clampCount("99"), clampCount(""), clampCount("abc")]).toEqual([10, 10, 50, 50, 20, 20]);
    expect(MAX_SETS).toBe(20);
  });

  test("every sets.* key exists in Persian, English and Dutch", () => {
    const keys = Object.keys(table("en")).filter((key) => key.startsWith("sets.") || key === "profile.sets");
    expect(keys.length).toBeGreaterThan(30);
    for (const lang of ["fa", "nl"] as const) expect(keys.filter((key) => !(key in table(lang)))).toEqual([]);
  });
});
