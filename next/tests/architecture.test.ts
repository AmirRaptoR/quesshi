import { describe, expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import path from "node:path";

const ROOT = path.join(import.meta.dir, "..");
const sources = [...new Bun.Glob("server/**/*.ts").scanSync(ROOT)]
  .filter((file) => !file.endsWith(".test.ts"))
  .map((file) => ({ file, text: readFileSync(path.join(ROOT, file), "utf8") }));

/** Every module a file imports, resolved to a path relative to the project root (packages stay as named). */
function imports(file: string, text: string): { target: string; typeOnly: boolean }[] {
  const found = [...text.matchAll(/^\s*import\s+(type\s+)?[^;]*?from\s+["']([^"']+)["']/gm)];
  return found.map((match) => {
    const specifier = match[2]!;
    const target = specifier.startsWith(".") ? path.join(path.dirname(file), specifier) : specifier;
    return { target, typeOnly: match[1] !== undefined };
  });
}

const featureOf = (file: string) => file.match(/^server\/features\/([^/]+)\//)?.[1];

describe("architecture", () => {
  test("the core never imports a feature", () => {
    const offenders = sources
      .filter(({ file }) => file.startsWith("server/core/"))
      .flatMap(({ file, text }) => imports(file, text).filter((i) => i.target.startsWith("server/features")).map((i) => `${file} → ${i.target}`));
    expect(offenders).toEqual([]);
  });

  test("a feature never imports another feature", () => {
    const offenders = sources.flatMap(({ file, text }) => {
      const own = featureOf(file);
      if (!own) return [];
      return imports(file, text)
        .filter((i) => i.target.startsWith("server/features/") && featureOf(`${i.target}/`) !== own)
        .map((i) => `${file} → ${i.target}`);
    });
    expect(offenders).toEqual([]);
  });

  test("SQL is written only in store files", () => {
    const sql = /["'`]\s*(select\s|insert\s+into|update\s+\w+\s+set|delete\s+from|create\s+(table|index)|alter\s+table|drop\s+table|pragma\s)/i;
    const allowed = (file: string) => file.endsWith(".store.ts") || file === "server/core/db.ts";
    const offenders = sources.filter(({ file, text }) => !allowed(file) && sql.test(text)).map(({ file }) => file);
    expect(offenders).toEqual([]);
  });

  test("Elysia is used only in route files", () => {
    const routeFile = (file: string) => file === "server/app.ts" || file.endsWith(".routes.ts") || /^server\/features\/[^/]+\/index\.ts$/.test(file);
    const offenders = sources
      .filter(({ file }) => !routeFile(file))
      .flatMap(({ file, text }) => imports(file, text).filter((i) => i.target === "elysia" && !i.typeOnly).map(() => file));
    expect(offenders).toEqual([]);
  });
});
