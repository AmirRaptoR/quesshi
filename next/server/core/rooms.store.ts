import type { Db } from "./db";

export type RoomsStore = ReturnType<typeof createRoomsStore>;

/** Snapshots of the rooms that are open or running, kept so a restart can bring them back. */
export function createRoomsStore(db: Db) {
  return {
    save(id: string, format: string, data: unknown): void {
      db.run("insert into room_snapshots (id, format, data) values (?, ?, ?) on conflict (id) do update set data = excluded.data", [id, format, JSON.stringify(data)]);
    },
    remove(id: string): void {
      db.run("delete from room_snapshots where id = ?", [id]);
    },
    of(format: string): unknown[] {
      return db.query<{ data: string }, [string]>("select data from room_snapshots where format = ?").all(format).map((row) => JSON.parse(row.data));
    },
  };
}
