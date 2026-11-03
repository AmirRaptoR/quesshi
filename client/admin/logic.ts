import { ApiError } from "../api";
import type { Translator } from "../i18n";

/** Back to what the admin was trying to reach, but only inside the admin panel. */
export function adminNext(next: string | undefined): string {
  return next && next.startsWith("/admin") ? next : "/admin";
}

/** Only a 401 means the admin is signed out; anything else may be passing, so the page offers a retry. */
export function sessionOutcome(error: unknown): "signed_out" | "unconfirmed" {
  return error instanceof ApiError && error.status === 401 ? "signed_out" : "unconfirmed";
}

export const PAGE_SIZES = [25, 50, 100] as const;

export const pageCount = (total: number, size: number) => Math.max(1, Math.ceil(total / size));

/** First, last, and a window of one around the current page; null marks a gap. */
export function pagerSlots(page: number, pages: number): (number | null)[] {
  const slots: (number | null)[] = [];
  let previous = 0;
  for (let number = 1; number <= pages; number++) {
    if (number !== 1 && number !== pages && Math.abs(number - page) > 1) continue;
    if (previous > 0 && number - previous > 1) slots.push(null);
    slots.push(number);
    previous = number;
  }
  return slots;
}

/** Removing the last row of the last page would leave an empty page; this is the page that still exists. */
export const clampPage = (page: number, total: number, size: number) => Math.min(Math.max(page, 1), pageCount(total, size));

/** The page that holds the current page's first row at the new size. */
export const pageAfterResize = (page: number, oldSize: number, newSize: number) => Math.floor(((page - 1) * oldSize) / newSize) + 1;

type Query = Readonly<Record<string, string | undefined>>;
const whole = (value: string | undefined, fallback: number) => (value && /^\d+$/.test(value) ? Number(value) : fallback);

export function readPaging(query: Query): { page: number; size: number } {
  const size = whole(query.size, 25);
  return { page: Math.max(1, whole(query.page, 1)), size: (PAGE_SIZES as readonly number[]).includes(size) ? size : 25 };
}

export interface QuestionFilters { lang: string; category: string; status: string; kind: string; level: number; text: string; page: number; size: number }

/** The address bar is the source of truth, so a shared or reloaded link opens the same view. */
export function readQuestionFilters(query: Query): QuestionFilters {
  const level = whole(query.level, 0);
  return {
    lang: query.lang ?? "", category: query.category ?? "", status: query.status ?? "", kind: query.kind ?? "",
    level: level >= 1 && level <= 5 ? level : 0, text: query.q ?? "", ...readPaging(query),
  };
}

const search = (pairs: [string, string | number | null][]) =>
  new URLSearchParams(pairs.filter((pair): pair is [string, string | number] => pair[1] !== null && pair[1] !== "").map(([k, v]) => [k, String(v)])).toString();

/** The filters for the address bar, leaving out every default. */
export function questionFilterSearch(f: QuestionFilters): string {
  return search([
    ["lang", f.lang], ["category", f.category], ["status", f.status], ["kind", f.kind], ["level", f.level || null], ["q", f.text.trim()],
    ["page", f.page === 1 ? null : f.page], ["size", f.size === 25 ? null : f.size],
  ]);
}

export function questionsApiQuery(f: QuestionFilters): string {
  return search([
    ["skip", (f.page - 1) * f.size], ["take", f.size], ["lang", f.lang], ["category", f.category], ["status", f.status], ["kind", f.kind],
    ["level", f.level || null], ["text", f.text.trim()],
  ]);
}

/** The text for a key, or the fallback's text when this language has none, as today's panel did. */
export function translateOr(t: Translator, key: string, fallback: string): string {
  const text = t.t(key);
  return text === key ? t.t(fallback) : text;
}

/** A weak password lists each broken rule; any other refusal shows its reason. */
export function problemsText(t: Translator, error: ApiError): string {
  const body = error.body as { problems?: unknown } | null;
  const problems = Array.isArray(body?.problems) ? body.problems.filter((p): p is string => typeof p === "string") : [];
  return problems.length ? problems.map((p) => t.t(p)).join(" ") : t.t(error.code);
}

/** "1 reports" reads like a bug, so the singular has its own text. */
export const reportLabel = (t: Translator, count: number) => (count === 1 ? t.t("admin.reportCountOne") : t.format("admin.reportCount", count));

/** Today's reason texts are keyed without the underscore. */
export const reportReasonKey = (reason: string) => `report.${reason.replace(/_/g, "")}`;

/** The bootstrap account ships with an address no mail can reach. */
export function isPlaceholderEmail(email: string): boolean {
  const lower = email.toLowerCase();
  return !email.includes("@") || lower.endsWith(".local") || lower.endsWith("@localhost");
}

const ONE_EMOJI = /^\p{RGI_Emoji}$/v;
// Unicode lists a skin tone on its own as an emoji; today's rule does not, since it shows as a brown square.
const LONE_MODIFIER = /^\p{Emoji_Modifier}$/u;

/** Exactly one emoji, as the server requires, so the form can say so before saving. */
export const isEmojiIcon = (value: string) => ONE_EMOJI.test(value.trim()) && !LONE_MODIFIER.test(value.trim());

/** A steady colour for a player's initial, from their avatar seed. */
export function tint(seed: string): string {
  let hash = 0;
  for (const char of seed) hash = (hash * 31 + char.charCodeAt(0)) | 0;
  return `hsl(${Math.abs(hash) % 360} 55% 62%)`;
}

const two = (n: number) => String(n).padStart(2, "0");

/** Today's local timestamp: yyyy-MM-dd HH:mm. */
export function stamp(value: string | number): string {
  const d = new Date(value);
  return `${d.getFullYear()}-${two(d.getMonth() + 1)}-${two(d.getDate())} ${two(d.getHours())}:${two(d.getMinutes())}`;
}

export const clock = (value: string | number) => stamp(value).slice(11);
