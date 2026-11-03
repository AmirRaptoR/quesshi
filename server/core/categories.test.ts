import { afterEach, describe, expect, test } from "bun:test";
import { testApp } from "../../tests/support/app";
import { defineFeature } from "./feature";
import { categoryIcon, selectCategories, slugify } from "./categories";

describe("category ids", () => {
  test("are made from the English name as today", () => {
    expect(slugify("  World History ")).toBe("world-history");
    expect(slugify("Food & Cooking")).toBe("food-cooking");
    expect(slugify("Sci_Fi 2")).toBe("sci_fi-2");
    expect(slugify("تاریخ")).toBe("");
  });
});

describe("category icons", () => {
  test("must be exactly one emoji", () => {
    for (const ok of ["🌍", "🇳🇱", "1️⃣", "❤️", "👍🏽", "👨‍👩‍👧"]) expect(categoryIcon(` ${ok} `)).toBe(ok);
    for (const bad of ["", "globe", "🌍🌍", "❤", "🇳"]) expect(categoryIcon(bad)).toBeNull();
  });
});

describe("a game's categories", () => {
  const eligible = ["a", "b", "c", "d"];

  test("are the player's eligible picks, once each, at most three", () => {
    expect(selectCategories(eligible, ["d", "x", "d", "a", "b", "c"], Math.random)).toEqual(["d", "a", "b"]);
  });

  test("are three eligible categories at random when the player picks none", () => {
    expect(selectCategories(eligible, [], () => 0.99)).toHaveLength(3);
    expect(new Set(selectCategories(eligible, [], () => 0)).size).toBe(3);
  });

  test("are none when no pick is eligible", () => {
    expect(selectCategories(eligible, ["x"], Math.random)).toEqual([]);
  });
});

let app: ReturnType<typeof testApp>;
afterEach(() => app?.cleanup());

const trivia = defineFeature({ name: "trivia" });
const polls = defineFeature({ name: "polls" });

async function asAdmin() {
  app = testApp([trivia, polls]);
  return app.signInAdmin();
}
const category = (fields: Record<string, unknown> = {}) => ({ id: "", nameFa: "تاریخ", nameEn: "History", nameNl: "Geschiedenis", icon: "📜", color: "#264653", isActive: true, sortOrder: 0, ...fields });

describe("admin categories", () => {
  test("are only for admins", async () => {
    await asAdmin();
    const player = await app.signIn("ann@example.com");

    expect((await app.call("GET", "/api/admin/categories", undefined, player)).status).toBe(401);
  });

  test("a new category gets its id from the English name and goes last; an edit keeps its place", async () => {
    const admin = await asAdmin();
    await app.call("POST", "/api/admin/categories", category({ nameEn: "Geography", icon: "🌍" }), admin);
    await app.call("POST", "/api/admin/categories", category(), admin);
    await app.call("POST", "/api/admin/categories", category({ id: "geography", nameEn: "World Geography", icon: "🗺️" }), admin);

    expect((await app.call("GET", "/api/admin/categories", undefined, admin)).body).toEqual([
      { id: "geography", nameFa: "تاریخ", nameEn: "World Geography", nameNl: "Geschiedenis", icon: "🗺️", color: "#264653", isActive: true, sortOrder: 1 },
      { id: "history", nameFa: "تاریخ", nameEn: "History", nameNl: "Geschiedenis", icon: "📜", color: "#264653", isActive: true, sortOrder: 2 },
    ]);
  });

  test("refuses a missing English name, a name that makes no id, and an icon that is not one emoji", async () => {
    const admin = await asAdmin();

    expect((await app.call("POST", "/api/admin/categories", category({ nameEn: " " }), admin)).body).toEqual({ error: "bad_name" });
    expect((await app.call("POST", "/api/admin/categories", category({ nameEn: "!!!" }), admin)).body).toEqual({ error: "bad_id" });
    expect((await app.call("POST", "/api/admin/categories", category({ icon: "scroll" }), admin)).body).toEqual({ error: "bad_icon" });
  });

  test("a category with questions cannot be deleted; an empty one leaves every feature's list", async () => {
    const admin = await asAdmin();
    await app.call("POST", "/api/admin/categories", category(), admin);
    await app.call("POST", "/api/admin/categories", category({ nameEn: "Sport", icon: "⚽" }), admin);
    await app.call("PUT", "/api/admin/content-settings", { trivia: ["history", "sport"], polls: ["sport"] }, admin);
    app.questions.add({ id: "q1", kind: "choice", lang: "en", categoryId: "history", level: 1, prompt: "?", payload: {}, explanation: null, status: "pending", createdAt: "2026-11-03T12:00:00Z" });

    expect((await app.call("DELETE", "/api/admin/categories/history", undefined, admin)).body).toEqual({ error: "category_in_use" });
    expect((await app.call("DELETE", "/api/admin/categories/sport", undefined, admin)).status).toBe(200);
    expect((await app.call("GET", "/api/admin/content-settings", undefined, admin)).body.settings).toEqual({ trivia: ["history"], polls: [] });
  });

  test("content settings keep only known categories, once each, for every enabled feature", async () => {
    const admin = await asAdmin();
    await app.call("POST", "/api/admin/categories", category(), admin);

    const saved = await app.call("PUT", "/api/admin/content-settings", { trivia: ["history", "history", "nope"], quiz: ["history"] }, admin);

    expect(saved.body).toEqual({ trivia: ["history"], polls: [] });
  });
});
