import { mkdirSync } from "node:fs";
import path from "node:path";
import { createApp } from "./app";
import { systemClock } from "./core/clock";
import { openDatabase } from "./core/db";
import { features } from "../features";

const dataDir = process.env.QUESSHI_DATA_DIR ?? path.join(import.meta.dir, "..", "data");
const port = Number(process.env.PORT ?? 3000);

mkdirSync(dataDir, { recursive: true });
const db = openDatabase(path.join(dataDir, "quesshi.sqlite"));
const secret = process.env.QUESSHI_SECRET;
if (!secret) throw new Error("Set QUESSHI_SECRET to a long random value; it signs sign-in codes.");
const origin = process.env.QUESSHI_ORIGIN ?? `http://localhost:${port}`;
// Until SMTP is configured, sign-in mail is written to the log.
const mailer = { send: async (message: { to: string; subject: string; text: string }) => console.log(`Mail to ${message.to}: ${message.subject}\n${message.text}`) };
const { server, migrations } = createApp({ db, clock: systemClock, mailer, secret, origin }, features);

for (const name of migrations.applied) console.log(`Applied migration ${name}`);
for (const owner of migrations.orphaned) console.warn(`Tables of removed feature "${owner}" remain in the database`);

server.listen(port);
console.log(`Quesshi listening on http://localhost:${port}`);
