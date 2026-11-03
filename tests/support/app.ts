import { createApp } from "../../server/app";
import type { Core, Feature } from "../../server/core/feature";
import { createQuestionsStore } from "../../server/core/questions.store";
import { tempWorld } from "./temp";
import { fakeScheduler, seededRandom } from "./time";

export const ORIGIN = "https://quesshi.example.com";
export const T0 = Date.parse("2026-11-03T12:00:00Z");
/** Today's ASP.NET Identity hash of ADMIN_PASSWORD, made by the real PasswordHasher. */
export const ADMIN_HASH = "AQAAAAIAAYagAAAAEIMhStRpzHj0KJcjTHOQ2gtXiOOLWmQwTF2A0150Y2lsYL0eWoa3SV5fIBgGcjM+kQ==";
export const ADMIN_PASSWORD = "Correct horse battery 9";

/**
 * The whole app on a temporary database, with a fake scheduler and captured mail.
 * `listen()` starts a real server on a free port for WebSocket tests.
 */
export function testApp(features: readonly Feature[], options: { bootstrapAdmin?: Core["bootstrapAdmin"] } = {}) {
  const world = tempWorld();
  const mail: { to: string; text: string }[] = [];
  const boot = (at: number, seed: number) => {
    const scheduler = fakeScheduler(at);
    const { server } = createApp({
      db: world.db, clock: scheduler.clock, schedule: scheduler.schedule, random: seededRandom(seed),
      secret: "test-secret", origin: ORIGIN, mediaDir: world.mediaDir, mailer: { send: async (message) => { mail.push(message); } },
      ...(options.bootstrapAdmin ? { bootstrapAdmin: options.bootstrapAdmin } : {}),
    }, features);
    return { scheduler, server };
  };
  let current = boot(T0, 1);
  const questions = createQuestionsStore(world.db);

  const call = async (method: string, path: string, body?: unknown, cookie?: string) => {
    const response = await current.server.handle(new Request(`${ORIGIN}${path}`, {
      method,
      headers: { "content-type": "application/json", origin: ORIGIN, ...(cookie ? { cookie } : {}) },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    }));
    const text = await response.text();
    const json = (() => { try { return JSON.parse(text); } catch { return null; } })();
    return { status: response.status, body: json, text, type: response.headers.get("content-type"), cookie: response.headers.get("set-cookie")?.split(";")[0] };
  };

  /** Sends a file as the raw request body, as the admin's import form does. */
  const upload = async (path: string, content: string | Uint8Array<ArrayBuffer>, contentType: string, cookie?: string) => {
    const response = await current.server.handle(new Request(`${ORIGIN}${path}`, {
      method: "POST", body: content, headers: { "content-type": contentType, origin: ORIGIN, ...(cookie ? { cookie } : {}) },
    }));
    return { status: response.status, body: await response.json().catch(() => null) };
  };

  return {
    world, questions, call, mail, upload,
    get scheduler() {
      return current.scheduler;
    },
    /**
     * Starts a new app on the same database at the current time, as a restart does: the old app's
     * timers stop with it, and only what the database kept comes back.
     */
    async restart() {
      await new Promise((resolve) => setTimeout(resolve, 0));
      current = boot(current.scheduler.clock.now().getTime(), 2);
    },
    /** Signs a player in through the emailed-code flow and returns their cookie. */
    async signIn(email: string): Promise<string> {
      await call("POST", "/api/auth/code", { email, lang: "en" });
      const code = mail.at(-1)!.text.match(/\b(\d{6})\b/)![1]!;
      return (await call("POST", "/api/auth/code/verify", { email, code, lang: "en" })).cookie!;
    },
    /** Adds the administrator root with today's hashed password, signs them in and returns their cookie. */
    async signInAdmin(): Promise<string> {
      world.db.run(
        "insert into admins (id, username, email, password_hash, created_at) values ('a1', 'root', 'root@example.com', ?, '2026-11-03T12:00:00Z')",
        [ADMIN_HASH],
      );
      return (await call("POST", "/api/admin/auth/login", { username: "root", password: ADMIN_PASSWORD })).cookie!;
    },
    /** Adds approved English choice questions at every level in a category every feature offers. */
    seedChoiceQuestions(count = 10) {
      questions.addCategory({ id: "geo", nameFa: "جغرافیا", nameEn: "Geography", nameNl: "Aardrijkskunde", icon: "🌍", color: "#2a9d8f" });
      for (const feature of features) questions.setOffers(feature.name, ["geo"]);
      for (let i = 0; i < count; i++) {
        questions.add({
          id: `q${i}`, kind: "choice", lang: "en", categoryId: "geo", level: (i % 5) + 1, prompt: `Question ${i}?`,
          payload: { options: ["A", "B", "C", "D"], correctIndex: 0 }, explanation: null, status: "approved", createdAt: new Date(T0).toISOString(),
        });
      }
    },
    /** Starts a real server on a free port; returns a WebSocket opener. */
    listen() {
      const { server } = current;
      server.listen(0);
      const port = server.server!.port;
      return {
        socket(path: string, cookie: string | undefined, origin = ORIGIN) {
          const ws = new WebSocket(`ws://localhost:${port}${path}`, { headers: { origin, ...(cookie ? { cookie } : {}) } } as unknown as string[]);
          const messages: unknown[] = [];
          const waiters: (() => void)[] = [];
          ws.addEventListener("message", (event) => {
            messages.push(JSON.parse(String(event.data)));
            waiters.splice(0).forEach((wake) => wake());
          });
          const opened = new Promise<void>((resolve, reject) => {
            ws.addEventListener("open", () => resolve());
            ws.addEventListener("error", () => reject(new Error("socket failed")));
            ws.addEventListener("close", () => reject(new Error("socket closed")));
          });
          return {
            ws, messages, opened,
            /** Waits (at most a second, as a hang guard) for a message matching the test. */
            async next(match: (message: any) => boolean): Promise<any> {
              const deadline = Date.now() + 1_000;
              for (;;) {
                const found = messages.find(match);
                if (found) {
                  messages.splice(messages.indexOf(found), 1);
                  return found;
                }
                if (Date.now() > deadline) throw new Error(`No matching message; got ${JSON.stringify(messages)}`);
                await new Promise<void>((resolve) => { waiters.push(resolve); setTimeout(resolve, 50); });
              }
            },
            send: (message: unknown) => ws.send(JSON.stringify(message)),
          };
        },
        close: () => server.stop(),
      };
    },
    /** Lets broadcasts queued for the next turn go out before the database closes. */
    async cleanup() {
      await new Promise((resolve) => setTimeout(resolve, 0));
      world.cleanup();
    },
  };
}
