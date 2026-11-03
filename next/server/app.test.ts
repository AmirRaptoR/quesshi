import { afterEach, describe, expect, test } from "bun:test";
import { Elysia } from "elysia";
import { tempWorld } from "../tests/support/temp";
import { createApp } from "./app";
import { defineFeature, FeatureError } from "./core/feature";

let world: ReturnType<typeof tempWorld>;
afterEach(() => world.cleanup());

const request = (path: string) => new Request(`http://localhost${path}`);

describe("createApp", () => {
  test("serves the health route", async () => {
    world = tempWorld();
    const { server } = createApp(world.core(), []);

    const response = await server.handle(request("/api/health"));

    expect(await response.json()).toEqual({ status: "ok" });
  });

  test("mounts a listed feature's routes under its prefix", async () => {
    world = tempWorld();
    const echo = defineFeature({ name: "echo", server: () => new Elysia({ prefix: "/f/echo" }).get("/hello", () => "hi") });

    const { server } = createApp(world.core(), [echo]);

    expect(await (await server.handle(request("/f/echo/hello"))).text()).toBe("hi");
  });

  test("a feature that is not listed is not served", async () => {
    world = tempWorld();
    const { server } = createApp(world.core(), []);

    expect((await server.handle(request("/f/echo/hello"))).status).toBe(404);
  });

  test("runs a listed feature's migrations under its own name", () => {
    world = tempWorld();
    const notes = defineFeature({ name: "notes", migrations: world.migrations("notes", { "001.sql": "create table notes_n (id text)" }) });

    const { migrations } = createApp(world.core(), [notes]);

    expect(migrations.applied).toContain("notes/001.sql");
  });

  test("refuses a feature whose routes are outside its prefix", () => {
    world = tempWorld();
    const rogue = defineFeature({ name: "rogue", server: () => new Elysia({ prefix: "/api" }).get("/x", () => "x") });

    expect(() => createApp(world.core(), [rogue])).toThrow(/must put its routes under the prefix \/f\/rogue/);
  });

  test("refuses two features that claim the same question kind", () => {
    world = tempWorld();
    const rules = { levelled: true, check: (payload: unknown) => ({ ok: true as const, payload }), columns: [], fromRow: () => ({}), example: {} };

    expect(() => createApp(world.core(), [defineFeature({ name: "one", kinds: { quiz: rules } }), defineFeature({ name: "two", kinds: { quiz: rules } })])).toThrow(FeatureError);
  });

  test("refuses a feature listed twice", () => {
    world = tempWorld();
    const one = defineFeature({ name: "twice" });

    expect(() => createApp(world.core(), [one, one])).toThrow(FeatureError);
  });

  test("refuses a badly formed or reserved feature name", () => {
    world = tempWorld();

    expect(() => createApp(world.core(), [defineFeature({ name: "Live_Reactions" })])).toThrow(/lowercase words/);
    expect(() => createApp(world.core(), [defineFeature({ name: "core" })])).toThrow(/reserved/);
  });
});
