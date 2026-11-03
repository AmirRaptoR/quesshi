import { afterEach, describe, expect, test } from "bun:test";
import { testApp } from "../../tests/support/app";

let app: ReturnType<typeof testApp>;
afterEach(() => app.cleanup());

const png: Uint8Array<ArrayBuffer> = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 1, 2, 3]);

describe("media uploads", () => {
  test("an admin uploads a file under a new name, and anyone can then load it", async () => {
    app = testApp([]);
    const admin = await app.signInAdmin();

    const uploaded = (await app.upload("/api/admin/media?name=../../Map%20Of%20NL.PNG", png, "application/octet-stream", admin)).body;

    expect(uploaded.kind).toBe("image");
    expect(uploaded.url).toMatch(/^\/media\/uploads\/[0-9a-f-]{36}\.png$/);
    const served = await app.call("GET", uploaded.url);
    expect(served.status).toBe(200);
    expect(served.type).toBe("image/png");
  });

  test("refuses an empty file and a type it does not know", async () => {
    app = testApp([]);
    const admin = await app.signInAdmin();

    expect((await app.upload("/api/admin/media?name=a.png", new Uint8Array(), "application/octet-stream", admin)).body).toEqual({ error: "bad_size" });
    expect((await app.upload("/api/admin/media?name=a.exe", png, "application/octet-stream", admin)).body).toEqual({ error: "bad_type" });
  });

  test("only admins upload, and names outside the uploads are never served", async () => {
    app = testApp([]);

    expect((await app.upload("/api/admin/media?name=a.png", png, "application/octet-stream")).status).toBe(401);
    expect((await app.call("GET", "/media/uploads/..%2Ftest.sqlite")).status).toBe(404);
  });
});
