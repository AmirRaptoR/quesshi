import { Database } from "bun:sqlite";

/** The one database handle the core opens and passes to everything that needs storage. */
export type Db = Database;

/** Opens the SQLite file with the settings every part of the app relies on. */
export function openDatabase(path: string): Db {
  const db = new Database(path, { create: true, strict: true });
  // WAL lets readers run while one writer commits; foreign keys are off by default in SQLite.
  db.run("pragma journal_mode = wal");
  db.run("pragma foreign_keys = on");
  db.run("pragma busy_timeout = 5000");
  db.run("pragma synchronous = normal");
  return db;
}
