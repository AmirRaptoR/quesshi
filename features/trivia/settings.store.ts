import type { Db } from "../../server/core/db";

export interface TriviaSettings { liveEnabled: boolean; maxCapacity: number }

/** Today's defaults: live on, lobbies of up to twenty. */
const DEFAULTS: TriviaSettings = { liveEnabled: true, maxCapacity: 20 };

export type SettingsStore = ReturnType<typeof createSettingsStore>;

export function createSettingsStore(db: Db) {
  return {
    get(): TriviaSettings {
      const rows = db.query<{ key: string; value: string }, []>("select key, value from trivia_settings").all();
      const stored = Object.fromEntries(rows.map((row) => [row.key, JSON.parse(row.value)]));
      return { ...DEFAULTS, ...stored };
    },
    save(changes: Partial<TriviaSettings>): void {
      db.transaction(() => {
        for (const [key, value] of Object.entries(changes)) {
          db.run("insert into trivia_settings (key, value) values (?, ?) on conflict (key) do update set value = excluded.value", [key, JSON.stringify(value)]);
        }
      })();
    },
  };
}
