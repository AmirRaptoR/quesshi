import { spawn, spawnSync } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { createServer } from "node:net";
import { tmpdir } from "node:os";
import path from "node:path";
import { expect, test as base, type BrowserContext, type Page } from "@playwright/test";
import { ADMIN } from "./admin";

const root = path.join(import.meta.dirname, "..");
const LANG_KEY = "quesshi.lang";

export type Lang = "fa" | "en";

/** One app process on its own port and data directory; stopped when the test ends. */
export interface App {
  origin: string;
  /** Waits for the next sign-in mail to the address that has not been read yet and returns its code. */
  codeFor(email: string): Promise<string>;
}

/** Helpers for signing in and for opening further players' browsers. */
export interface Quesshi {
  /** Signs a player in through the emailed-code flow and lands on their home page. */
  signIn(page: Page, email: string): Promise<void>;
  /** Signs the seeded administrator in to a browser context. */
  signInAdmin(context: BrowserContext): Promise<void>;
  /** Opens another browser context for a further player, in the same language and on the same phone. */
  newPlayerContext(): Promise<BrowserContext>;
}

async function freePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const probe = createServer();
    probe.once("error", reject);
    probe.listen(0, "127.0.0.1", () => {
      const { port } = probe.address() as { port: number };
      probe.close(() => resolve(port));
    });
  });
}

/** Seeds a fresh data directory, then starts the app on a free port and waits until it listens. */
async function startApp(): Promise<{ app: App; stop(): Promise<void> }> {
  const dataDir = mkdtempSync(path.join(tmpdir(), "quesshi-e2e-"));
  const seeded = spawnSync("bun", ["e2e/seed.ts", dataDir], { cwd: root, encoding: "utf8" });
  if (seeded.status !== 0) throw new Error(`Seeding failed:\n${seeded.stdout}${seeded.stderr}`);

  const port = await freePort();
  const origin = `http://localhost:${port}`;
  const child = spawn("bun", ["server/main.ts"], {
    cwd: root,
    env: { ...process.env, PORT: String(port), QUESSHI_DATA_DIR: dataDir, QUESSHI_SECRET: "e2e-secret", QUESSHI_ORIGIN: origin, SMTP_HOST: "" },
    stdio: ["ignore", "pipe", "pipe"],
  });

  // Without SMTP_HOST the app writes each mail to its log; reading that log is the test mailer.
  let log = "";
  let read = 0;
  const watchers = new Set<() => void>();
  const onData = (chunk: Buffer) => {
    log += chunk.toString();
    for (const wake of [...watchers]) wake();
  };
  child.stdout.on("data", onData);
  child.stderr.on("data", onData);
  const died = new Promise<never>((_, reject) => child.once("exit", (code) => reject(new Error(`The app exited with ${code}:\n${log}`))));
  died.catch(() => {});

  /** Resolves when the log first satisfies `find`, with no polling: every chunk of output wakes the check. */
  const untilLogged = <T>(find: () => T | undefined): Promise<T> => new Promise<T>((resolve, reject) => {
    const check = () => {
      const found = find();
      if (found === undefined) return;
      watchers.delete(check);
      resolve(found);
    };
    watchers.add(check);
    died.catch((error) => { watchers.delete(check); reject(error); });
    check();
  });

  await untilLogged(() => (log.includes("Quesshi listening") ? true : undefined));

  const app: App = {
    origin,
    codeFor: (email) => untilLogged(() => {
      const mail = new RegExp(`Mail to ${email.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}: [\\s\\S]*?\\b(\\d{6})\\b`, "g");
      mail.lastIndex = read;
      const match = mail.exec(log);
      if (!match) return undefined;
      read = mail.lastIndex;
      return match[1]!;
    }),
  };
  return {
    app,
    async stop() {
      const closed = new Promise<void>((resolve) => child.once("exit", () => resolve()));
      child.kill();
      await closed;
      rmSync(dataDir, { recursive: true, force: true });
    },
  };
}

/** Chooses the language on a browser's first visit, the way a player's settings would. */
async function chooseLanguage(context: BrowserContext, lang: Lang): Promise<void> {
  await context.addInitScript(([key, value]) => {
    if (!localStorage.getItem(key!)) localStorage.setItem(key!, value!);
  }, [LANG_KEY, lang]);
}

export const test = base.extend<{ appLang: Lang; app: App; quesshi: Quesshi }>({
  appLang: ["fa", { option: true }],
  app: async ({}, use) => {
    const started = await startApp();
    try {
      await use(started.app);
    } finally {
      await started.stop();
    }
  },
  baseURL: async ({ app }, use) => { await use(app.origin); },
  context: async ({ context, appLang }, use) => {
    await chooseLanguage(context, appLang);
    await use(context);
  },
  quesshi: async ({ app, browser, appLang }, use) => {
    const opened: BrowserContext[] = [];
    try {
      await use({
        async signIn(page, email) {
          await page.goto("/signin");
          await page.locator("#email").fill(email);
          await page.locator(".btn--block:not(.btn--ghost)").click();
          const code = await app.codeFor(email);
          await page.locator(".code-input").fill(code);
          await page.locator(".btn--block:not(.btn--ghost)").click();
          await expect(page).toHaveURL(/\/play$/);
        },
        async signInAdmin(target) {
          const response = await target.request.post(`${app.origin}/api/admin/auth/login`, {
            headers: { origin: app.origin }, data: { username: ADMIN.username, password: ADMIN.password },
          });
          expect(response.ok()).toBe(true);
        },
        async newPlayerContext() {
          const extra = await browser.newContext({ viewport: { width: 360, height: 740 }, baseURL: app.origin });
          opened.push(extra);
          await chooseLanguage(extra, appLang);
          return extra;
        },
      });
    } finally {
      for (const extra of opened) await extra.close();
    }
  },
});

export { expect };
