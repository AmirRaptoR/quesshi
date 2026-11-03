import { afterEach, describe, expect, test } from "bun:test";
import { fixedClock, tempWorld } from "../../tests/support/temp";
import { MigrationError, runMigrations } from "./migrations";

let world: ReturnType<typeof tempWorld>;
afterEach(() => world.cleanup());

describe("runMigrations", () => {
  test("applies the core first, then each feature, files in name order", () => {
    world = tempWorld();
    const core = world.migrations("core", { "002_b.sql": "create table b (id text)", "001_a.sql": "create table a (id text)" });
    const trivia = world.migrations("trivia", { "001_t.sql": "create table trivia_t (id text)" });

    const result = runMigrations(world.db, [{ owner: "core", directory: core }, { owner: "trivia", directory: trivia }], fixedClock);

    expect(result.applied).toEqual(["core/001_a.sql", "core/002_b.sql", "trivia/001_t.sql"]);
  });

  test("runs a file with several statements", () => {
    world = tempWorld();
    const core = world.migrations("core", { "001.sql": "create table a (id text);\ncreate table b (id text);\ninsert into a values ('x');" });

    runMigrations(world.db, [{ owner: "core", directory: core }], fixedClock);

    expect(world.db.query("select id from a").all()).toEqual([{ id: "x" }]);
    expect(world.db.query("select count(*) as n from b").get()).toEqual({ n: 0 });
  });

  test("a second run applies nothing", () => {
    world = tempWorld();
    const core = world.migrations("core", { "001.sql": "create table a (id text)" });
    runMigrations(world.db, [{ owner: "core", directory: core }], fixedClock);

    expect(runMigrations(world.db, [{ owner: "core", directory: core }], fixedClock).applied).toEqual([]);
  });

  test("a failing file leaves no table and no ledger row", () => {
    world = tempWorld();
    const core = world.migrations("core", { "001.sql": "create table a (id text);\nthis is not sql;" });

    expect(() => runMigrations(world.db, [{ owner: "core", directory: core }], fixedClock)).toThrow(MigrationError);
    expect(world.db.query("select name from sqlite_master where name = 'a'").all()).toEqual([]);
    expect(world.db.query("select count(*) as n from migrations").get()).toEqual({ n: 0 });
  });

  test("reports a removed feature's recorded migrations as orphaned", () => {
    world = tempWorld();
    const core = world.migrations("core", { "001.sql": "create table a (id text)" });
    const polls = world.migrations("polls", { "001.sql": "create table polls_p (id text)" });
    runMigrations(world.db, [{ owner: "core", directory: core }, { owner: "polls", directory: polls }], fixedClock);

    const result = runMigrations(world.db, [{ owner: "core", directory: core }], fixedClock);

    expect(result.orphaned).toEqual(["polls"]);
    expect(world.db.query("select name from sqlite_master where name = 'polls_p'").all()).toEqual([{ name: "polls_p" }]);
  });

  test("refuses to start when an applied migration's file is gone", () => {
    world = tempWorld();
    const before = world.migrations("core", { "001.sql": "create table a (id text)" });
    runMigrations(world.db, [{ owner: "core", directory: before }], fixedClock);
    const after = world.migrations("core-without-001", {});

    expect(() => runMigrations(world.db, [{ owner: "core", directory: after }], fixedClock)).toThrow(/001.sql was applied but its file is missing/);
  });
});
