import { afterEach, describe, expect, test } from "bun:test";
import { testApp } from "../../tests/support/app";

let app: ReturnType<typeof testApp>;
afterEach(() => app.cleanup());

describe("admin users", () => {
  test("lists members newest first, without guests, searching name or email", async () => {
    app = testApp([]);
    const admin = await app.signInAdmin();
    await app.signIn("ann@example.com");
    app.scheduler.advanceTo(Date.parse("2026-11-03T12:01:00Z"));
    await app.signIn("bob@example.com");
    app.world.db.run("insert into players (id, display_name, avatar_seed, lang, guest, created_at) values ('g1', 'Guesty', 'avatar-0', 'en', 1, '2026-11-03T12:02:00Z')");
    const names = async (query: string) => (await app.call("GET", `/api/admin/users?${query}`, undefined, admin)).body;

    expect(await names("")).toMatchObject({ total: 2, items: [{ displayName: "bob", email: "bob@example.com", banned: false }, { displayName: "ann" }] });
    expect((await names("q=ANN")).items.map((u: { displayName: string }) => u.displayName)).toEqual(["ann"]);
    expect(await names("skip=1&take=1")).toMatchObject({ total: 2, items: [{ displayName: "ann" }] });
  });

  test("a ban signs the player out at once and keeps them out; lifting it lets them back", async () => {
    app = testApp([]);
    const admin = await app.signInAdmin();
    const ann = await app.signIn("ann@example.com");
    const id = (await app.call("GET", "/api/me", undefined, ann)).body.id;

    await app.call("POST", `/api/admin/users/${id}/ban`, { value: true }, admin);
    expect((await app.call("GET", "/api/me", undefined, ann)).status).toBe(401);
    expect((await app.call("GET", "/api/admin/users", undefined, admin)).body.items[0].banned).toBe(true);

    await app.call("POST", `/api/admin/users/${id}/ban`, { value: false }, admin);
    expect((await app.call("GET", "/api/me", undefined, await app.signIn("ann@example.com"))).status).toBe(200);
  });

  test("banning someone who does not exist is not found", async () => {
    app = testApp([]);
    const admin = await app.signInAdmin();

    expect((await app.call("POST", "/api/admin/users/nobody/ban", { value: true }, admin)).status).toBe(404);
  });
});
