import { describe, expect, test } from "bun:test";
import { render } from "preact-render-to-string";
import { translator, type Lang } from "../../../../client/i18n";
import { SetPicker, type HostSet } from "./set-picker";

const sets: HostSet[] = [
  { id: "s1", name: "Movies", lang: "en", questionCount: 12 },
  { id: "s2", name: "فیلم‌ها", lang: "fa", questionCount: 7 },
];

/** The real translation table of a language, as the browser loads it. */
async function rendered(lang: Lang, value: string | null) {
  const table = (await Bun.file(new URL(`../../../../client/public/i18n/${lang}.json`, import.meta.url)).json()) as Record<string, string>;
  const t = translator(lang, table);
  return { t, html: render(<SetPicker t={t} sets={sets} value={value} onChange={() => {}} />) };
}

describe("the question set picker", () => {
  test("in English it offers each of the host's sets as a chip beside the categories", async () => {
    const { t, html } = await rendered("en", null);

    expect(t.rtl).toBe(false);
    expect(html).toContain("Or play from one of your question sets");
    expect(html).toContain("Movies · 12");
    expect(html).toContain("فیلم‌ها · 7");
    expect(html.match(/aria-pressed="false"/g)).toHaveLength(2);
  });

  test("in Persian (right to left) it is worded in Persian with Persian digits, and marks the chosen set", async () => {
    const { t, html } = await rendered("fa", "s2");

    expect(t.rtl).toBe(true);
    expect(html).toContain("یا از یکی از مجموعه‌پرسش‌های خودتان بازی کنید");
    expect(html).toContain("فیلم‌ها · ۷");
    expect(html).toContain("Movies · ۱۲");
    expect(html).toMatch(/is-on" aria-pressed="true"[^>]*>فیلم‌ها/);
    expect(html).toContain("فقط پرسش‌های همین مجموعه استفاده می‌شود");
  });

  test("a host without sets is offered nothing", () => {
    const t = translator("en", {});
    expect(render(<SetPicker t={t} sets={[]} value={null} onChange={() => {}} />)).toBe("");
  });
});
