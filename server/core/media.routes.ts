import { mkdirSync } from "node:fs";
import path from "node:path";
import { Elysia } from "elysia";
import type { AdminAuth } from "./admin";
import { adminOnly } from "./admin.routes";

const MAX_UPLOAD_BYTES = 20 * 1024 * 1024;
/** Today's accepted media, by extension. */
const KINDS: Record<string, "image" | "audio" | "video"> = {
  ".png": "image", ".jpg": "image", ".jpeg": "image", ".webp": "image", ".gif": "image",
  ".mp3": "audio", ".m4a": "audio", ".ogg": "audio", ".mp4": "video", ".webm": "video",
};
const SERVED = /^[0-9a-f-]{36}\.[a-z0-9]+$/;

/** Admins upload question media; everyone loads it from /media/uploads. */
export function mediaRoutes(auth: AdminAuth, mediaDir: string) {
  const folder = path.join(mediaDir, "uploads");
  return new Elysia()
    .use(adminOnly(auth, "/api/admin").post("/media", async ({ request, query, set }) => {
      const extension = path.extname(query.name ?? "").toLowerCase();
      const content = new Uint8Array(await request.arrayBuffer());
      if (content.length === 0 || content.length > MAX_UPLOAD_BYTES) {
        set.status = 400;
        return { error: "bad_size" };
      }
      const kind = KINDS[extension];
      if (!kind) {
        set.status = 400;
        return { error: "bad_type" };
      }
      // Never trust the uploaded name: our own name keeps every file inside the uploads folder.
      const name = `${crypto.randomUUID()}${extension}`;
      mkdirSync(folder, { recursive: true });
      await Bun.write(path.join(folder, name), content);
      return { kind, url: `/media/uploads/${name}` };
    }, { parse: "none" }))
    .get("/media/uploads/:name", async ({ params, set }) => {
      const file = SERVED.test(params.name) ? Bun.file(path.join(folder, params.name)) : null;
      if (!file || !(await file.exists())) {
        set.status = 404;
        return { error: "not_found" };
      }
      return file;
    });
}
