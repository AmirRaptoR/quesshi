import { afterEach, describe, expect, test } from "bun:test";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { Elysia } from "elysia";
import { clientRoutes } from "./client.routes";

let dir: string;
afterEach(() => rmSync(dir, { recursive: true, force: true }));

function built() {
  dir = mkdtempSync(path.join(tmpdir(), "quesshi-client-"));
  mkdirSync(path.join(dir, "assets"));
  writeFileSync(path.join(dir, "index.html"), "<!doctype html><div id=app></div>");
  writeFileSync(path.join(dir, "assets", "main-abc123.js"), "console.log(1)");
  const app = new Elysia().get("/api/health", () => ({ status: "ok" })).use(clientRoutes(dir));
  return (url: string) => app.handle(new Request(`https://quesshi.example.com${url}`));
}

describe("the client", () => {
  test("serves built files, and the app page for any other address", async () => {
    const get = built();

    const script = await get("/assets/main-abc123.js");
    expect(await script.text()).toBe("console.log(1)");
    expect(script.headers.get("cache-control")).toContain("immutable");
    const page = await get("/play/questions");
    expect(page.headers.get("content-type")).toStartWith("text/html");
    expect(page.headers.get("cache-control")).toBe("no-cache");
    expect(await page.text()).toContain('<div id=app>');
  });

  test("leaves the API to its own routes, and never serves files outside the build", async () => {
    const get = built();

    expect(await (await get("/api/health")).json()).toEqual({ status: "ok" });
    expect((await get("/api/nope")).status).toBe(404);
    expect((await get("/assets/..%2F..%2Fetc%2Fpasswd")).status).toBe(404);
  });
});
