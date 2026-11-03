import { readdirSync, readFileSync, existsSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import type { Clock } from "./clock";
import type { Db } from "./db";
import { createMigrationsStore } from "./migrations.store";

/** A set of forward-only SQL files belonging to one owner: the core or a feature. */
export interface MigrationSource {
  owner: string;
  directory: URL;
}

export interface MigrationResult {
  applied: string[];
  /** Owners with recorded migrations that are no longer loaded: a removed feature whose tables remain. */
  orphaned: string[];
}

export class MigrationError extends Error {}

/** The core's own migrations, applied before any feature's. */
export const CORE_MIGRATIONS = new URL("./migrations/", import.meta.url);

/** Applies every missing migration, owner by owner in the given order, files in name order. */
export function runMigrations(db: Db, sources: readonly MigrationSource[], clock: Clock): MigrationResult {
  const ledger = createMigrationsStore(db);
  const applied: string[] = [];
  for (const source of sources) {
    const files = migrationFiles(source.directory);
    const done = ledger.applied(source.owner);
    for (const file of files) {
      if (done.has(file)) continue;
      const sql = readFileSync(path.join(fileURLToPath(source.directory), file), "utf8");
      try {
        ledger.apply(source.owner, file, sql, clock.now().toISOString());
      } catch (error) {
        throw new MigrationError(`${source.owner}: migration ${file} failed: ${error instanceof Error ? error.message : String(error)}`, { cause: error });
      }
      applied.push(`${source.owner}/${file}`);
    }
  }
  const loaded = new Set(sources.map((source) => source.owner));
  return { applied, orphaned: ledger.owners().filter((owner) => !loaded.has(owner)) };
}

function migrationFiles(directory: URL): string[] {
  const folder = fileURLToPath(directory);
  if (!existsSync(folder)) return [];
  return readdirSync(folder).filter((file) => file.endsWith(".sql")).sort();
}
