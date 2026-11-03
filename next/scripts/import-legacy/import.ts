import { copyFileSync, existsSync, mkdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { features } from "../../features";
import type { Clock } from "../../server/core/clock";
import type { Db } from "../../server/core/db";
import type { KindRules } from "../../server/core/kind";
import { CORE_MIGRATIONS, runMigrations } from "../../server/core/migrations";
import { createQuestionsStore, type Media } from "../../server/core/questions.store";

/** Today's enums, by their stored numbers. */
const LANGS = ["fa", "en", "nl"];
const STATUSES = ["pending", "approved", "rejected"];
const MEDIA_KINDS: (Media["kind"] | null)[] = [null, "image", "audio", "video"];
const TRIVIA = 0;
const KIND_CHOICE = 0, KIND_SORT = 1, KIND_MAP = 2, KIND_PLAYERS = 3;
const VOTING_FIXED = 1;
const TARGET_COUNTRY = 0;

type Doc = Record<string, unknown>;
export interface ImportReport { imported: number; skipped: { id: string; reason: string }[] }

/** mongoexport writes dates as {"$date": "..."} (or a number of milliseconds) in its JSON. */
function isoDate(value: unknown): string {
  const raw = value && typeof value === "object" && "$date" in value ? (value as { $date: unknown }).$date : value;
  const millis = raw && typeof raw === "object" && "$numberLong" in raw ? Number((raw as { $numberLong: string }).$numberLong) : raw;
  return new Date(millis as string | number).toISOString();
}

const strings = (value: unknown) => (Array.isArray(value) ? value.filter((v): v is string => typeof v === "string") : []);

/** A document's kind and payload in the new shape, before the kind's own check. */
function shape(doc: Doc): { kind: string; payload: unknown } {
  if (doc.Family !== TRIVIA) {
    return doc.AnswerSource === VOTING_FIXED ? { kind: "poll_choice", payload: { options: strings(doc.FixedChoices) } } : { kind: "poll_roster", payload: {} };
  }
  const choices = strings(doc.Choices);
  switch (doc.Kind ?? KIND_CHOICE) {
    case KIND_SORT: return { kind: "order", payload: { items: choices.map((text, i) => ({ id: String(i + 1), text })) } };
    case KIND_MAP: return {
      kind: "map_pick",
      payload: {
        baseLayer: doc.BaseLayer === 1 ? "borders" : "blank",
        target: doc.TargetShape === TARGET_COUNTRY
          ? { shape: "country", countryCode: doc.TargetCountryCode }
          : { shape: "point", latitude: doc.TargetLatitude, longitude: doc.TargetLongitude, radiusKm: doc.TargetRadiusKm },
      },
    };
    // Today's "players" trivia kind has no right answer: it is a poll of the players.
    case KIND_PLAYERS: return { kind: "poll_roster", payload: {} };
    default: return { kind: "choice", payload: { options: choices, correctIndex: doc.CorrectIndex } };
  }
}

/**
 * Imports today's categories, content settings and question bank from a mongoexport folder
 * (categories.json, content_settings.json, questions.json, and the site's media folder) into the
 * new database. Players, history and reports stay behind. Running it again adds only what is new.
 */
export function importLegacy({ exportDir, db, mediaDir, clock }: { exportDir: string; db: Db; mediaDir: string; clock: Clock }): ImportReport {
  runMigrations(db, [
    { owner: "core", directory: CORE_MIGRATIONS },
    ...features.flatMap((feature) => (feature.migrations ? [{ owner: feature.name, directory: feature.migrations }] : [])),
  ], clock);
  const kinds = new Map<string, KindRules>(features.flatMap((feature) => Object.entries(feature.kinds ?? {})));
  const read = (name: string): Doc[] => {
    const file = path.join(exportDir, `${name}.json`);
    return existsSync(file) ? (JSON.parse(readFileSync(file, "utf8")) as Doc[]) : [];
  };
  const store = createQuestionsStore(db);
  const report: ImportReport = { imported: 0, skipped: [] };

  db.transaction(() => {
    for (const c of read("categories")) {
      store.saveCategory({
        id: String(c._id), nameFa: String(c.NameFa ?? ""), nameEn: String(c.NameEn ?? ""), nameNl: String(c.NameNl ?? ""),
        icon: String(c.Icon ?? ""), color: String(c.Color ?? ""), isActive: c.IsActive !== false, sortOrder: Number(c.SortOrder ?? 0),
      });
    }
    const settings = read("content_settings")[0];
    if (settings) {
      store.setOffers("trivia", strings(settings.TriviaCategoryIds));
      store.setOffers("polls", strings(settings.VotingCategoryIds));
    }

    for (const doc of read("questions")) {
      const id = String(doc._id);
      if (store.question(id)) continue;
      const skip = (reason: string) => report.skipped.push({ id, reason });
      // Players' own questions belong to packs, which come later.
      if (doc.OwnerId) {
        skip("player_authored");
        continue;
      }
      const { kind, payload } = shape(doc);
      const rules = kinds.get(kind)!;
      const checked = rules.check(payload);
      if (!checked.ok) {
        skip(checked.error);
        continue;
      }
      const media = copyMedia(doc, exportDir, mediaDir);
      if (media === "missing") {
        skip("missing_media");
        continue;
      }
      store.add({
        id, kind, lang: LANGS[Number(doc.Lang)] ?? "fa", categoryId: typeof doc.CategoryId === "string" ? doc.CategoryId : null,
        level: rules.levelled ? Number(doc.Level) : null, prompt: String(doc.Prompt ?? "").trim(), payload: checked.payload,
        explanation: typeof doc.Explanation === "string" && doc.Explanation.trim() ? doc.Explanation.trim() : null, media,
        status: STATUSES[Number(doc.Status)] ?? "pending", createdAt: isoDate(doc.CreatedAt),
      });
      report.imported++;
    }
  })();
  return report;
}

/** A local file is copied into the uploads under a new name; a link to another site is kept. */
function copyMedia(doc: Doc, exportDir: string, mediaDir: string): Media | null | "missing" {
  const kind = MEDIA_KINDS[Number(doc.MediaKind ?? 0)] ?? null;
  const url = typeof doc.MediaUrl === "string" ? doc.MediaUrl.trim() : "";
  if (!kind || !url) return null;
  const attribution = typeof doc.MediaAttribution === "string" && doc.MediaAttribution.trim() ? doc.MediaAttribution.trim() : null;
  if (/^https?:\/\//.test(url)) return { kind, url, attribution };
  const source = path.join(exportDir, path.normalize(url).replace(/^([/\\])+/, ""));
  if (!source.startsWith(path.resolve(exportDir)) || !existsSync(source)) return "missing";
  const name = `${crypto.randomUUID()}${path.extname(source).toLowerCase()}`;
  mkdirSync(path.join(mediaDir, "uploads"), { recursive: true });
  copyFileSync(source, path.join(mediaDir, "uploads", name));
  return { kind, url: `/media/uploads/${name}`, attribution };
}
