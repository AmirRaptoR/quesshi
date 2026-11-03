export type Lang = "fa" | "en" | "nl";
export const LANGS: readonly Lang[] = ["fa", "en", "nl"];

const PERSIAN_ZERO = 0x06f0;

export interface Translator {
  lang: Lang;
  rtl: boolean;
  t(key: string): string;
  /** The text with {0}, {1}… replaced by the arguments; number arguments in the language's digits. */
  format(key: string, ...args: (string | number)[]): string;
  /** A number or text with its digits in the language's own. */
  num(value: string | number): string;
  name(category: { nameFa: string; nameEn: string; nameNl: string }): string;
}

/** Today's translator over one language's table. */
export function translator(lang: Lang, table: Readonly<Record<string, string>>): Translator {
  const num = (value: string | number) =>
    lang === "fa" ? String(value).replace(/[0-9]/g, (digit) => String.fromCharCode(PERSIAN_ZERO + Number(digit))) : String(value);
  const t = (key: string) => table[key] ?? key;
  const prefer = (first: string, second: string) => (first.length > 0 ? first : second);
  return {
    lang,
    rtl: table["app.dir"] === "rtl",
    t,
    num,
    format: (key, ...args) => t(key).replace(/\{(\d+)\}/g, (match, index: string) => {
      const value = args[Number(index)];
      // Only numbers change digits: a name like "Ali2" is the player's own spelling.
      return value === undefined ? match : typeof value === "number" ? num(value) : value;
    }),
    name: (c) => (lang === "fa" ? prefer(c.nameFa, c.nameEn) : lang === "nl" ? prefer(c.nameNl, c.nameEn) : prefer(c.nameEn, c.nameFa)),
  };
}

/** Loads a language's table; a failed load leaves the keys showing rather than breaking the page. */
export async function loadTranslator(lang: Lang): Promise<Translator> {
  const table = await fetch(`/i18n/${lang}.json`).then((r) => (r.ok ? r.json() : {})).catch(() => ({}));
  return translator(lang, table as Record<string, string>);
}
