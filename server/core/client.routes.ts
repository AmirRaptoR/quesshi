import path from "node:path";
import { Elysia, type AnyElysia } from "elysia";

/** Addresses that are never the app's pages; unknown ones there are plain 404s. */
const NOT_PAGES = /^\/(api|f|media)(\/|$)/;

/** Serves the built client: its files, and index.html for every page address so the router takes over. */
export function clientRoutes(clientDir: string): AnyElysia {
  const root = path.resolve(clientDir);
  const index = Bun.file(path.join(root, "index.html"));
  return new Elysia().get("/*", async ({ path: requested, set }) => {
    const target = path.resolve(root, `.${decodeURIComponent(requested)}`);
    if (target.startsWith(root + path.sep)) {
      const file = Bun.file(target);
      if (await file.exists()) {
        // Built assets carry a content hash in their name, so they never change under that name.
        set.headers["cache-control"] = requested.startsWith("/assets/") ? "public, max-age=31536000, immutable" : "no-cache";
        return file;
      }
    }
    if (NOT_PAGES.test(requested) || path.extname(requested)) {
      set.status = 404;
      return { error: "not_found" };
    }
    set.headers["cache-control"] = "no-cache";
    set.headers["content-type"] = "text/html; charset=utf-8";
    return index;
  });
}
