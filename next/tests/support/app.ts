import { createApp } from "../../server/app";
import type { Core, Feature } from "../../server/core/feature";
import { createQuestionsStore } from "../../server/core/questions.store";
import { tempWorld } from "./temp";
import { fakeScheduler, fixedRandom } from "./time";

export const ORIGIN = "https://quesshi.example.com";
export const T0 = Date.parse("2026-11-03T12:00:00Z");

/**
 * The whole app on a temporary database, with a fake scheduler and captured mail.
 * `listen()` starts a real server on a free port for WebSocket tests.
 */
export function testApp(features: readonly Feature[], options: { bootstrapAdmin?: Core["bootstrapAdmin"] } = {}) {
  const world = tempWorld();
  const scheduler = fakeScheduler(T0);
  const mail: { to: string; text: string }[] = [];
  const { server } = createApp({
    db: world.db, clock: scheduler.clock, schedule: scheduler.schedule, random: fixedRandom,
    secret: "test-secret", origin: ORIGIN, mailer: { send: async (message) => { mail.push(message); } },
    ...(options.bootstrapAdmin ? { bootstrapAdmin: options.bootstrapAdmin } : {}),
  }, features);
  const questions = createQuestionsStore(world.db);

  const call = async (method: string, path: string, body?: unknown, cookie?: string) => {
    const response = await server.handle(new Request(`${ORIGIN}${path}`, {
      method,
      headers: { "content-type": "application/json", origin: ORIGIN, ...(cookie ? { cookie } : {}) },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    }));
    return { status: response.status, body: await response.json().catch(() => null), cookie: response.headers.get("set-cookie")?.split(";")[0] };
  };

  return {
    world, scheduler, questions, call, mail,
    /** Signs a player in through the emailed-code flow and returns their cookie. */
    async signIn(email: string): Promise<string> {
      await call("POST", "/api/auth/code", { email, lang: "en" });
      const code = mail.at(-1)!.text.match(/\b(\d{6})\b/)![1]!;
      return (await call("POST", "/api/auth/code/verify", { email, code, lang: "en" })).cookie!;
    },
    /** Adds approved English choice questions at every level, enough for any short game. */
    seedChoiceQuestions(count = 10) {
      questions.addCategory({ id: "geo", nameFa: "جغرافیا", nameEn: "Geography", nameNl: "Aardrijkskunde", icon: "globe", color: "#2a9d8f" });
      for (let i = 0; i < count; i++) {
        questions.add({
          id: `q${i}`, kind: "choice", lang: "en", categoryId: "geo", level: (i % 5) + 1, prompt: `Question ${i}?`,
          payload: { options: ["A", "B", "C", "D"], correctIndex: 0 }, explanation: null, status: "approved", createdAt: new Date(T0).toISOString(),
        });
      }
    },
    /** Starts a real server on a free port; returns a WebSocket opener. */
    listen() {
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
    cleanup() {
      world.cleanup();
    },
  };
}
