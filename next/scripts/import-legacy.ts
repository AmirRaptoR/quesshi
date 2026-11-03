import path from "node:path";
import { systemClock } from "../server/core/clock";
import { openDatabase } from "../server/core/db";
import { importLegacy } from "./import-legacy/import";

/**
 * Imports today's question bank into a data folder. Export it first, where today's MongoDB runs:
 *   for c in categories content_settings questions; do mongoexport --db quesshi --collection $c --jsonArray --out $c.json; done
 * and copy the site's wwwroot/media folder next to the files as media/.
 * Usage: bun scripts/import-legacy.ts <export folder> <data folder>
 */
const [exportDir, dataDir] = process.argv.slice(2);
if (!exportDir || !dataDir) throw new Error("Usage: bun scripts/import-legacy.ts <export folder> <data folder>");
const db = openDatabase(path.join(dataDir, "quesshi.sqlite"));
const report = importLegacy({ exportDir: path.resolve(exportDir), db, mediaDir: path.join(dataDir, "media"), clock: systemClock });
console.log(`Imported ${report.imported} questions; skipped ${report.skipped.length}.`);
const reasons = Map.groupBy(report.skipped, (entry) => entry.reason);
for (const [reason, entries] of reasons) console.log(`  ${reason}: ${entries.length} (e.g. ${entries.slice(0, 3).map((e) => e.id).join(", ")})`);
db.close();
