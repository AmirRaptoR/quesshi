import { afterEach, describe, expect, test } from "bun:test";
import { Elysia } from "elysia";
import { fixedClock, tempWorld } from "../tests/support/temp";
import { createApp } from "./app";
import { defineFeature, FeatureError } from "./core/feature";

let world: ReturnType<typeof tempWorld>;
afterEach(() => world.cleanup());

const request = (path: string) => new Request(`http://localhost${path}`);

describe("createApp", () => {
  test("serves the health route", async () => {
    world = tempWorld();
    const { server } = createApp({ db: world.db, clock: fixedClock }, []);

    const response = await server.handle(request("/api/health"));

    expect(await response.json()).toEqual({ status: "ok" });
  });

  test("mounts a listed feature's routes under its prefix", async () => {
    world = tempWorld();
    const echo = defineFeature({ name: "echo", server: () => new Elysia({ prefix: "/f/echo" }).get("/hello", () => "hi") });

    const { server } = createApp({ db: world.db, clock: fixedClock }, [echo]);

    expect(await (await server.handle(request("/f/echo/hello"))).text()).toBe("hi");
  });

  test("a feature that is not listed is not served", async () => {
    world = tempWorld();
    const { server } = createApp({ db: world.db, clock: fixedClock }, []);

    expect((await server.handle(request("/f/echo/hello"))).status).toBe(404);
  });

  test("runs a listed feature's migrations under its own name", () => {
    world = tempWorld();
    const notes = defineFeature({ name: "notes", migrations: world.migrations("notes", { "001.sql": "create table notes_n (id text)" }) });

    const { migrations } = createApp({ db: world.db, clock: fixedClock }, [notes]);

    expect(migrations.applied).toContain("notes/001.sql");
  });

  test("refuses a feature whose routes are outside its prefix", () => {
    world = tempWorld();
    const rogue = defineFeature({ name: "rogue", server: () => new Elysia({ prefix: "/api" }).get("/x", () => "x") });

    expect(() => createApp({ db: world.db, clock: fixedClock }, [rogue])).toThrow(/must put its routes under the prefix \/f\/rogue/);
  });

  test("refuses a feature listed twice", () => {
    world = tempWorld();
    const one = defineFeature({ name: "twice" });

    expect(() => createApp({ db: world.db, clock: fixedClock }, [one, one])).toThrow(FeatureError);
  });

  test("refuses a badly formed or reserved feature name", () => {
    world = tempWorld();

    expect(() => createApp({ db: world.db, clock: fixedClock }, [defineFeature({ name: "Live_Reactions" })])).toThrow(/lowercase words/);
    expect(() => createApp({ db: world.db, clock: fixedClock }, [defineFeature({ name: "core" })])).toThrow(/reserved/);
  });
});
