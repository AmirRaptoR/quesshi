import path from "node:path";
import { createApp } from "../server/app";
import { openDatabase } from "../server/core/db";
import { createQuestionsStore, MIN_APPROVED_QUESTIONS_PER_LANGUAGE } from "../server/core/questions.store";
import { features } from "../features";
import { ADMIN } from "./admin";

const CATEGORIES = [
  { id: "geo", nameFa: "جغرافیا", nameEn: "Geography", nameNl: "Aardrijkskunde", icon: "🌍", color: "#2a9d8f" },
  { id: "sci", nameFa: "علوم", nameEn: "Science", nameNl: "Wetenschap", icon: "🔬", color: "#e76f51" },
  { id: "art", nameFa: "هنر", nameEn: "Art", nameNl: "Kunst", icon: "🎨", color: "#7b61ff" },
] as const;

/**
 * Fills an empty data directory the way the app would on its first start, plus a few categories
 * with enough approved questions in both languages and one administrator. Usage:
 * `bun e2e/seed.ts <data dir>`.
 */
const dataDir = process.argv[2];
if (!dataDir) throw new Error("Usage: bun e2e/seed.ts <data dir>");

const db = openDatabase(path.join(dataDir, "quesshi.sqlite"));
const clock = { now: () => new Date() };
// Building the app applies every migration and creates the administrator; it is never served.
createApp({
  db, clock, schedule: () => () => {}, random: Math.random, mailer: { send: async () => {} }, secret: "e2e-secret",
  origin: "http://localhost", mediaDir: path.join(dataDir, "media"), bootstrapAdmin: ADMIN,
}, features);

const questions = createQuestionsStore(db);
const createdAt = clock.now().toISOString();
for (const category of CATEGORIES) {
  questions.addCategory(category);
  for (const lang of ["fa", "en"] as const) {
    for (let i = 0; i < MIN_APPROVED_QUESTIONS_PER_LANGUAGE; i++) {
      questions.add({
        id: `${category.id}-${lang}-${i}`, kind: "choice", lang, categoryId: category.id, level: (i % 5) + 1,
        prompt: `${category.nameEn} ${i}?`, payload: { options: ["A", "B", "C", "D"], correctIndex: 0 },
        explanation: null, status: "approved", createdAt,
      });
    }
  }
}
for (const feature of features) questions.setOffers(feature.name, CATEGORIES.map((category) => category.id));
db.close();
