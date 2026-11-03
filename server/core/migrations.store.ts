import type { Db } from "./db";

/** The ledger of applied migrations, one row per owner (the core or a feature) and file. */
export function createMigrationsStore(db: Db) {
  db.run(`create table if not exists migrations (
    owner text not null,
    name text not null,
    applied_at text not null,
    primary key (owner, name)
  )`);
  return {
    applied(owner: string): Set<string> {
      const rows = db.query<{ name: string }, [string]>("select name from migrations where owner = ?").all(owner);
      return new Set(rows.map((row) => row.name));
    },
    owners(): string[] {
      return db.query<{ owner: string }, []>("select distinct owner from migrations order by owner").all().map((row) => row.owner);
    },
    /** Runs one migration and records it in the same transaction, so a failed file leaves no trace. */
    apply(owner: string, name: string, sql: string, appliedAt: string): void {
      db.transaction(() => {
        db.run(sql);
        db.run("insert into migrations (owner, name, applied_at) values (?, ?, ?)", [owner, name, appliedAt]);
      })();
    },
  };
}
