import { parseCsv } from "./csv";
import { numberCell, type KindRules } from "./kind";
import type { Media } from "./questions.store";

const LANGS = ["fa", "en", "nl"];
const STATUSES = ["pending", "approved", "rejected"];
const MAX_IMPORT_BYTES = 20 * 1024 * 1024;
const MAX_IMPORT_ROWS = 2000;
const SHARED_COLUMNS = ["lang", "categoryid", "level", "prompt"];
const TAIL_COLUMNS = ["explanation", "status"];

export interface KindEntry { feature: string; rules: KindRules }
export interface QuestionFields {
  kind: string; lang: string; categoryId: string | null; level: number | null; prompt: string; payload: unknown; explanation: string | null; media: Media | null; status: string;
}
const MEDIA_KINDS = ["image", "audio", "video"];
type Result<T> = { ok: true; value: T } | { ok: false; error: string };

const text = (value: unknown) => (typeof value === "string" ? value.trim() : "");

/** Checks one authored question; each rejection is a code the admin form translates and shows by its field. */
export function checkQuestion(input: Record<string, unknown>, kinds: ReadonlyMap<string, KindEntry>, categoryIds: readonly string[]): Result<QuestionFields> {
  const fail = (error: string) => ({ ok: false as const, error });
  const kind = text(input.kind);
  const entry = kinds.get(kind);
  if (!entry) return fail("bad_kind");
  const lang = text(input.lang);
  if (!LANGS.includes(lang)) return fail("bad_lang");
  const level = input.level ?? null;
  const levelOk = entry.rules.levelled ? Number.isInteger(level) && (level as number) >= 1 && (level as number) <= 5 : level === null;
  if (!levelOk) return fail("bad_level");
  const prompt = text(input.prompt);
  if (!prompt) return fail("bad_prompt");
  const categoryId = text(input.categoryId) || null;
  if (categoryId && !categoryIds.includes(categoryId)) return fail("bad_category");
  const status = input.status === undefined ? "pending" : text(input.status);
  if (!STATUSES.includes(status)) return fail("bad_status");
  let media: Media | null = null;
  if (input.media !== undefined && input.media !== null) {
    const { kind: mediaKind, url, attribution } = input.media as Record<string, unknown>;
    if (typeof mediaKind !== "string" || !MEDIA_KINDS.includes(mediaKind)) return fail("bad_media_kind");
    if (!text(url)) return fail("bad_media_url");
    media = { kind: mediaKind as Media["kind"], url: text(url), attribution: text(attribution) || null };
  }
  const checked = entry.rules.check(input.payload);
  if (!checked.ok) return fail(checked.error);
  return { ok: true, value: { kind, lang, categoryId, level: level as number | null, prompt, payload: checked.payload, explanation: text(input.explanation) || null, media, status } };
}

export interface ImportReport {
  total: number;
  accepted: number;
  rejected: number;
  rows: { row: number; prompt: string | null; accepted: boolean; error: string | null }[];
}

/** Reads a CSV or JSON file of one kind's questions, as today's importer: rows are checked one by one. */
export function readImport(kind: string, format: string, content: string, kinds: ReadonlyMap<string, KindEntry>, categoryIds: readonly string[]): Result<{ report: ImportReport; questions: QuestionFields[] }> {
  const entry = kinds.get(kind);
  if (!entry) return { ok: false, error: "bad_kind" };
  if (format !== "csv" && format !== "json") return { ok: false, error: "bad_format" };
  if (!content.trim()) return { ok: false, error: "empty_file" };
  if (Buffer.byteLength(content) > MAX_IMPORT_BYTES) return { ok: false, error: "file_too_large" };
  const required = ["lang", "categoryid", "prompt", ...(entry.rules.levelled ? ["level"] : []), ...entry.rules.columns];
  const rows = format === "csv" ? csvRows(content, required) : jsonRows(content);
  if (!rows.ok) return rows;
  if (rows.value.length > MAX_IMPORT_ROWS) return { ok: false, error: "too_many_rows" };

  const report: ImportReport = { total: rows.value.length, accepted: 0, rejected: 0, rows: [] };
  const questions: QuestionFields[] = [];
  rows.value.forEach((row, index) => {
    const result = row
      ? checkQuestion({
        kind, lang: row.lang, categoryId: row.categoryid, prompt: row.prompt, explanation: row.explanation, status: row.status,
        level: row.level === undefined ? null : (numberCell(row.level) ?? Number.NaN), payload: entry.rules.fromRow(row),
        // As today, media without a kind is an image.
        media: row.mediaurl === undefined ? null : { kind: row.mediakind ?? "image", url: row.mediaurl },
      }, kinds, categoryIds)
      : { ok: false as const, error: "bad_row" };
    if (result.ok) questions.push(result.value);
    report.rows.push({ row: index + 1, prompt: row?.prompt ?? null, accepted: result.ok, error: result.ok ? null : result.error });
  });
  report.accepted = questions.length;
  report.rejected = report.total - report.accepted;
  return { ok: true, value: { report, questions } };
}

/** The import template: the shared columns, the kind's columns and one example row. */
export function importTemplate(rules: KindRules, format: "csv" | "json"): string {
  const { prompt, ...own } = rules.example;
  const row: Record<string, string> = { lang: "en", categoryId: "geography", ...(rules.levelled ? { level: "2" } : {}), prompt: prompt ?? "", ...own, explanation: "", status: "pending" };
  if (format === "json") return JSON.stringify([row], null, 2);
  const cell = (value: string) => (/[",\r\n]/.test(value) ? `"${value.replace(/"/g, '""')}"` : value);
  return [Object.keys(row), Object.values(row)].map((line) => line.map(cell).join(",")).join("\r\n") + "\r\n";
}

type Row = Record<string, string> | null;

/** Lowercase keys, trimmed values, and no empty cells. */
function clean(entries: [string, unknown][]): Record<string, string> {
  const row: Record<string, string> = {};
  for (const [key, value] of entries) {
    const cell = typeof value === "string" ? value.trim() : typeof value === "number" || typeof value === "boolean" ? String(value) : "";
    if (cell) row[key.trim().toLowerCase()] = cell;
  }
  return row;
}

function csvRows(content: string, required: readonly string[]): Result<Row[]> {
  const [header, ...records] = parseCsv(content);
  const columns = header?.map((column) => column.trim().toLowerCase());
  if (!columns || !required.every((column) => columns.includes(column))) return { ok: false, error: "bad_row" };
  return { ok: true, value: records.map((record) => (record ? clean(columns.map((column, i) => [column, record[i] ?? ""])) : null)) };
}

function jsonRows(content: string): Result<Row[]> {
  let parsed: unknown;
  try {
    parsed = JSON.parse(content);
  } catch {
    return { ok: false, error: "bad_json" };
  }
  if (!Array.isArray(parsed)) return { ok: false, error: "bad_json" };
  return { ok: true, value: parsed.map((item) => (item && typeof item === "object" && !Array.isArray(item) ? clean(Object.entries(item)) : null)) };
}
