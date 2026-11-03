import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { pathToFileURL } from "node:url";
import type { Clock } from "../../server/core/clock";
import { openDatabase, type Db } from "../../server/core/db";

/** A temporary directory with a real SQLite file, removed by cleanup(). */
export function tempWorld() {
  const root = mkdtempSync(path.join(tmpdir(), "quesshi-test-"));
  const db: Db = openDatabase(path.join(root, "test.sqlite"));
  return {
    db,
    /** Writes migration files into a folder of the temporary directory and returns its URL. */
    migrations(name: string, files: Record<string, string>): URL {
      const folder = path.join(root, name);
      mkdirSync(folder, { recursive: true });
      for (const [file, sql] of Object.entries(files)) writeFileSync(path.join(folder, file), sql);
      return pathToFileURL(folder + path.sep);
    },
    cleanup(): void {
      db.close();
      rmSync(root, { recursive: true, force: true });
    },
  };
}

export const fixedClock: Clock = { now: () => new Date("2026-11-03T12:00:00.000Z") };
