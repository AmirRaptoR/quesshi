import { LANGS, type Lang, type Translator } from "../../../../client/i18n";
import { stored } from "../../../../client/storage";
import { range, rangeLevels, withHigh, withLow, WHOLE, type DifficultyRange } from "./difficulty-range";

/** What a new game is drawn with, as the lobby routes take it. */
/** With a questionSetId the game is drawn from that one of the host's question sets, and categories and levels do not apply. */
export interface DuelSettings { lang: string; questions: number; categoryIds: string[]; levels: number[]; questionSetId?: string }

export const QUESTION_COUNTS = [10, 20, 30, 40, 50, 100];
const KEY = "quesshi.duelSettings";
const HOME = "/play";

const isLang = (value: unknown): value is Lang => LANGS.includes(value as Lang);
const strings = (value: unknown) => Array.isArray(value) && value.every((v) => typeof v === "string");
const numbers = (value: unknown) => Array.isArray(value) && value.every((v) => Number.isInteger(v));

/** The remembered settings; anything unreadable is the default, a preference nobody can read being simply absent. */
export function readStored(raw: string | null, lang: Lang): DuelSettings {
  const fallback = { lang, questions: 10, categoryIds: [], levels: [] };
  try {
    const value = JSON.parse(raw ?? "null") as Partial<DuelSettings> | null;
    if (!value || !isLang(value.lang) || !Number.isInteger(value.questions) || !strings(value.categoryIds) || !numbers(value.levels)) return fallback;
    const base = { lang: value.lang, questions: value.questions!, categoryIds: value.categoryIds!, levels: value.levels! };
    return typeof value.questionSetId === "string" ? { ...base, questionSetId: value.questionSetId } : base;
  } catch {
    return fallback;
  }
}

export const lastSettings = (lang: Lang) => readStored(stored.get(KEY), lang);
export const remember = (settings: DuelSettings) => stored.set(KEY, JSON.stringify(settings));

export function summary(settings: DuelSettings) {
  const levels = [...new Set(settings.levels.filter((l) => l >= 1 && l <= 5))].sort();
  const mixed = levels.length === 0 || levels.length === 5;
  return { questions: settings.questions, mixed, from: mixed ? null : levels[0]!, to: mixed ? null : levels.at(-1)!, topics: settings.categoryIds.length };
}

/** The difficulty as the line and the slider both word it: Mixed, one level's name, or a span. */
export function difficultyText(s: ReturnType<typeof summary>, t: Translator): string {
  if (s.mixed) return t.t("home.settingsMixed");
  return s.from === s.to ? t.t(`level.${s.from}`) : `${t.t(`level.${s.from}`)}–${t.t(`level.${s.to}`)}`;
}

/** Today's one line standing for the whole question form: how long, how hard, on what. */
export function settingsLine(settings: DuelSettings, t: Translator): string {
  const s = summary(settings);
  if (settings.questionSetId) return [t.format("home.settingsQuestions", s.questions), t.t("home.settingsFromSet")].join(t.t("home.settingsSeparator"));
  const topics = s.topics === 0 ? t.t("home.settingsAllTopics") : t.format("home.settingsTopics", s.topics);
  return [t.format("home.settingsQuestions", s.questions), difficultyText(s, t), topics].join(t.t("home.settingsSeparator"));
}

/** The questions sheet's state while it is edited; it lives in the address, so nothing changes until Done. */
export interface Draft {
  lang: string;
  questions: number;
  levels: DifficultyRange;
  categoryIds: string[];
  /** One of the host's own question sets, instead of categories and levels. */
  questionSetId: string | null;
  returnTo: string;
  /** The lobby the owner opened the sheet from; Done also puts the settings to it. */
  lobby: { id: string; format: string } | null;
}

/** Only a path inside the app; a scheme-relative address would make the sheet an open redirect. */
export const safeReturn = (value: string | null) =>
  value && value.startsWith("/") && !value.startsWith("//") && !value.startsWith("/\\") ? value : HOME;

export function readDraft(search: string, saved: DuelSettings): Draft {
  const query = new URLSearchParams(search);
  const lang = query.get("lang");
  const count = Number(query.get("n"));
  const lo = query.get("lo");
  const hi = query.get("hi");
  const levels = lo !== null && hi !== null && /^-?\d+$/.test(lo) && /^-?\d+$/.test(hi) ? withLow(withHigh(WHOLE, Number(hi)), Number(lo)) : range(saved.levels);
  const topics = query.get("t");
  const lobby = query.get("lb");
  const set = query.get("qs");
  return {
    lang: isLang(lang) ? lang : saved.lang,
    questions: Number.isInteger(count) && count > 0 ? count : saved.questions,
    levels,
    // Present but empty is "any topic", a real answer.
    categoryIds: topics === null ? saved.categoryIds : topics.split(",").map((id) => id.trim()).filter(Boolean),
    questionSetId: set === null ? saved.questionSetId ?? null : set || null,
    returnTo: safeReturn(query.get("r")),
    lobby: lobby ? { id: lobby, format: query.get("lf") ?? "live" } : null,
  };
}

export function draftLink(path: string, draft: Draft): string {
  const query = new URLSearchParams({
    lang: draft.lang, n: String(draft.questions), lo: String(draft.levels.low), hi: String(draft.levels.high), t: draft.categoryIds.join(","), r: draft.returnTo,
    // Present but empty is "no set", a real answer.
    qs: draft.questionSetId ?? "",
    ...(draft.lobby ? { lb: draft.lobby.id, lf: draft.lobby.format } : {}),
  });
  return `${path}?${query}`;
}

export const toSettings = (draft: Draft): DuelSettings => ({
  lang: draft.lang, questions: draft.questions, categoryIds: draft.categoryIds, levels: rangeLevels(draft.levels),
  ...(draft.questionSetId ? { questionSetId: draft.questionSetId } : {}),
});
