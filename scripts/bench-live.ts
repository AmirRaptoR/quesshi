/**
 * Benchmarks live trivia: N phones in each of G games, on the real server in its own process.
 * Seeds a fresh data folder, starts the server, joins everyone over HTTP, opens a socket per phone,
 * and answers every question at once. Prints join and connect times, how long a question takes to
 * reach every phone, how long an answer takes to be acknowledged, and the server's memory and CPU.
 *
 * Usage: bun scripts/bench-live.ts [players per game = 500] [games = 1] [questions = 3]
 */
import { Database } from "bun:sqlite";
import { createHash, randomBytes } from "node:crypto";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";

const PLAYERS = Number(process.argv[2] ?? 500);
const GAMES = Number(process.argv[3] ?? 1);
const QUESTIONS = Number(process.argv[4] ?? 3);
const PORT = 3990;
const ORIGIN = `http://localhost:${PORT}`;
const root = path.join(import.meta.dir, "..");
const dataDir = mkdtempSync(path.join(tmpdir(), "quesshi-bench-"));

const percentile = (values: number[], p: number) => {
  const sorted = [...values].sort((a, b) => a - b);
  return sorted.length ? sorted[Math.min(sorted.length - 1, Math.floor((p / 100) * sorted.length))]! : NaN;
};
const ms = (value: number) => `${value.toFixed(0)} ms`;

// Start once to migrate, then seed while it is stopped.
async function startServer() {
  const server = Bun.spawn(["bun", path.join(root, "server", "main.ts")], {
    env: { ...process.env, QUESSHI_SECRET: "bench", QUESSHI_DATA_DIR: dataDir, PORT: String(PORT), QUESSHI_ORIGIN: ORIGIN },
    stdout: "ignore", stderr: "inherit",
  });
  for (let i = 0; i < 100; i++) {
    if (await fetch(`${ORIGIN}/api/health`).then((r) => r.ok).catch(() => false)) return server;
    await Bun.sleep(100);
  }
  throw new Error("The server did not start.");
}

let server = await startServer();
server.kill();
await server.exited;

const db = new Database(path.join(dataDir, "quesshi.sqlite"));
const now = new Date().toISOString();
const later = new Date(Date.now() + 86400_000).toISOString();
const sha = (v: string) => createHash("sha256").update(v).digest("hex");
db.run("insert into categories (id, name_fa, name_en, name_nl, icon, color) values ('geo', 'جغرافیا', 'Geography', 'Aardrijkskunde', '🌍', '#2a9d8f')");
db.run("insert into category_offers (feature, category_id) values ('trivia', 'geo')");
db.run("insert into trivia_settings (key, value) values ('maxCapacity', '500')");
for (let i = 0; i < 50; i++) {
  db.run("insert into questions (id, kind, lang, category_id, level, prompt, payload, status, created_at) values (?, 'choice', 'en', 'geo', ?, ?, ?, 'approved', ?)",
    [`q${i}`, (i % 5) + 1, `Question ${i}?`, JSON.stringify({ options: ["A", "B", "C", "D"], correctIndex: 0 }), now]);
}
const cookies: string[] = [];
db.transaction(() => {
  for (let i = 0; i < PLAYERS * GAMES; i++) {
    const id = `p${i}`;
    const token = randomBytes(24).toString("base64url");
    db.run("insert into players (id, email, display_name, avatar_seed, lang, created_at) values (?, ?, ?, 'avatar-0', 'en', ?)", [id, `p${i}@example.com`, `Player ${i}`, now]);
    db.run("insert into auth_sessions (token_hash, player_id, created_at, expires_at) values (?, ?, ?, ?)", [sha(token), id, now, later]);
    cookies.push(`__Host-quesshi=${token}`);
  }
})();
db.close();

server = await startServer();
const usage = () => {
  const status = readFileSync(`/proc/${server.pid}/status`, "utf8");
  const rss = Number(/VmRSS:\s+(\d+)/.exec(status)?.[1] ?? 0) / 1024;
  const stat = readFileSync(`/proc/${server.pid}/stat`, "utf8").split(" ");
  const cpu = (Number(stat[13]) + Number(stat[14])) / 100;
  return { rss, cpu };
};
const call = (method: string, url: string, cookie: string, body?: unknown) =>
  fetch(`${ORIGIN}${url}`, { method, headers: { cookie, origin: ORIGIN, "content-type": "application/json" }, ...(body ? { body: JSON.stringify(body) } : {}) }).then((r) => r.json() as Promise<Record<string, unknown>>);

async function inBatches<T>(items: T[], size: number, run: (item: T) => Promise<unknown>) {
  for (let i = 0; i < items.length; i += size) await Promise.all(items.slice(i, i + size).map(run));
}

interface Phone { ws: WebSocket; seen: Set<number>; received: number; bytes: number }
const fanout: number[] = [];
const acks: number[] = [];
const before = usage();
const started = performance.now();
const games: { id: string; owner: string; phones: Phone[] }[] = [];

for (let g = 0; g < GAMES; g++) {
  const mine = cookies.slice(g * PLAYERS, (g + 1) * PLAYERS);
  const owner = mine[0]!;
  const lobby = await call("POST", "/f/trivia/live/lobby", owner, { lang: "en", questions: QUESTIONS, categoryIds: [], levels: [], capacity: PLAYERS });
  if (!lobby.id) throw new Error(`Could not open a lobby: ${JSON.stringify(lobby)}`);
  await inBatches(mine.slice(1), 50, (cookie) => call("POST", `/f/trivia/live/join/${lobby.code}`, cookie));
  games.push({ id: String(lobby.id), owner, phones: [] });
}
const joined = performance.now();

for (const [g, game] of games.entries()) {
  const mine = cookies.slice(g * PLAYERS, (g + 1) * PLAYERS);
  await inBatches(mine, 100, (cookie) => new Promise<void>((resolve, reject) => {
    const ws = new WebSocket(`ws://localhost:${PORT}/f/trivia/live/${game.id}/socket`, { headers: { cookie, origin: ORIGIN } } as unknown as string[]);
    const phone: Phone = { ws, seen: new Set(), received: 0, bytes: 0 };
    const sentAt = new Map<number, number>();
    ws.onmessage = (event) => {
      phone.received++;
      phone.bytes += String(event.data).length;
      const message = JSON.parse(String(event.data));
      if (message.type === "answered") acks.push(performance.now() - sentAt.get(message.slot)!);
      const view = message.view?.game;
      if (message.type !== "view" || view?.phase !== "question" || phone.seen.has(view.slot)) return;
      phone.seen.add(view.slot);
      // questionEndsAt is the server's wall clock; the server opened the question 20 s earlier.
      fanout.push(Date.now() - (view.questionEndsAt - 20_000));
      sentAt.set(view.slot, performance.now());
      ws.send(JSON.stringify({ type: "answer", slot: view.slot, answer: { index: 0 } }));
    };
    ws.onopen = () => resolve();
    ws.onerror = () => reject(new Error("A socket failed."));
    game.phones.push(phone);
  }));
}
const connected = performance.now();
const afterConnect = usage();

for (const game of games) await call("POST", `/f/trivia/live/${game.id}/start`, game.owner);
const startedGames = performance.now();
let peakRss = afterConnect.rss;
// Countdown, then each question (20 s plus 2 s grace at most) and its 3 s reveal.
const deadline = Date.now() + 3_000 + QUESTIONS * 25_000 + 5_000;
while (Date.now() < deadline) {
  peakRss = Math.max(peakRss, usage().rss);
  if (games.every((game) => game.phones.every((phone) => phone.seen.size === QUESTIONS)) && acks.length >= PLAYERS * GAMES * QUESTIONS) break;
  await Bun.sleep(250);
}
const after = usage();
const phones = games.flatMap((game) => game.phones);
const messages = phones.reduce((sum, phone) => sum + phone.received, 0);
const bytes = phones.reduce((sum, phone) => sum + phone.bytes, 0);

console.log(`${GAMES} game(s) × ${PLAYERS} players, ${QUESTIONS} questions`);
console.log(`join over HTTP: ${ms(joined - started)}; open sockets: ${ms(connected - joined)}`);
console.log(`question reached a phone after: p50 ${ms(percentile(fanout, 50))}, p95 ${ms(percentile(fanout, 95))}, max ${ms(percentile(fanout, 100))} (${fanout.length} of ${PLAYERS * GAMES * QUESTIONS})`);
console.log(`answer acknowledged after: p50 ${ms(percentile(acks, 50))}, p95 ${ms(percentile(acks, 95))}, max ${ms(percentile(acks, 100))} (${acks.length})`);
console.log(`messages to phones: ${messages} (${(bytes / 1024 / 1024).toFixed(1)} MB), ${(messages / (PLAYERS * GAMES)).toFixed(0)} per phone`);
console.log(`server memory: ${before.rss.toFixed(0)} MB idle, ${afterConnect.rss.toFixed(0)} MB connected, ${peakRss.toFixed(0)} MB peak`);
console.log(`server CPU: ${(after.cpu - afterConnect.cpu).toFixed(1)} s over ${((performance.now() - startedGames) / 1000).toFixed(1)} s of play`);

for (const phone of phones) phone.ws.close();
server.kill();
await server.exited;
rmSync(dataDir, { recursive: true, force: true });
process.exit(0);
