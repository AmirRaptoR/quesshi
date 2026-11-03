import { describe, expect, test } from "bun:test";
import { translator } from "./i18n";

const fa = translator("fa", { "app.dir": "rtl", "live.round": "پرسش {0} از {1}" });
const en = translator("en", { "app.dir": "ltr", "live.round": "Question {0} of {1}" });

describe("the translator", () => {
  test("looks up a key, and shows the key itself when it is missing", () => {
    expect(en.t("live.round")).toBe("Question {0} of {1}");
    expect(en.t("nope.missing")).toBe("nope.missing");
  });

  test("fills numbered places, numbers in Persian digits for Persian", () => {
    expect(en.format("live.round", 3, 10)).toBe("Question 3 of 10");
    expect(fa.format("live.round", 3, 10)).toBe("پرسش ۳ از ۱۰");
    expect(fa.format("live.round", "Ali2", 10)).toBe("پرسش Ali2 از ۱۰");
  });

  test("knows the direction and writes numbers in the language's digits", () => {
    expect(fa.rtl).toBe(true);
    expect(en.rtl).toBe(false);
    expect(fa.num(2026)).toBe("۲۰۲۶");
    expect(en.num(2026)).toBe("2026");
  });

  test("names a category in the language, falling back to English or Persian", () => {
    const category = { nameFa: "تاریخ", nameEn: "History", nameNl: "" };
    expect(fa.name(category)).toBe("تاریخ");
    expect(translator("nl", {}).name(category)).toBe("History");
    expect(en.name({ ...category, nameEn: "" })).toBe("تاریخ");
  });
});
