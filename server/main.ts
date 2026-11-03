import { mkdirSync } from "node:fs";
import path from "node:path";
import { createApp } from "./app";
import { systemClock } from "./core/clock";
import { openDatabase } from "./core/db";
import { realSchedule } from "./core/schedule";
import { features } from "../features";
import { mailerFromEnv } from "./smtp";

const dataDir = process.env.QUESSHI_DATA_DIR ?? path.join(import.meta.dir, "..", "data");
const port = Number(process.env.PORT ?? 3000);

mkdirSync(dataDir, { recursive: true });
const db = openDatabase(path.join(dataDir, "quesshi.sqlite"));
const secret = process.env.QUESSHI_SECRET;
if (!secret) throw new Error("Set QUESSHI_SECRET to a long random value; it signs sign-in codes.");
const origin = process.env.QUESSHI_ORIGIN ?? `http://localhost:${port}`;
const mailer = mailerFromEnv(process.env);
if (!process.env.SMTP_HOST) console.warn("SMTP_HOST is not set: sign-in mail is written to the log instead of being sent.");
const googleClientId = process.env.GOOGLE_CLIENT_ID;
// Today's check: Google's token endpoint must vouch for the token, for our client, with a verified email.
const google = googleClientId ? {
  clientId: googleClientId,
  async verify(idToken: string) {
    const response = await fetch(`https://oauth2.googleapis.com/tokeninfo?id_token=${encodeURIComponent(idToken)}`).catch(() => null);
    if (!response?.ok) return null;
    const token = await response.json() as { aud?: string; email_verified?: string; email?: string; name?: string };
    if (token.aud !== googleClientId || token.email_verified !== "true" || !token.email) return null;
    return { email: token.email, name: token.name ?? null };
  },
} : undefined;
const { server, migrations } = createApp({ db, clock: systemClock, schedule: realSchedule(systemClock), random: Math.random, mailer, secret, origin, mediaDir: path.join(dataDir, "media"), version: process.env.QUESSHI_VERSION ?? "dev",
  // Today's first administrator: created on an empty database; without a password one is generated and logged.
  bootstrapAdmin: { username: process.env.ADMIN_USERNAME ?? "admin", email: process.env.ADMIN_EMAIL ?? "admin@quesshi.local", password: process.env.ADMIN_PASSWORD || null }, clientDir: path.join(import.meta.dir, "..", "dist", "client"), ...(google ? { google } : {}) }, features);

for (const name of migrations.applied) console.log(`Applied migration ${name}`);
for (const owner of migrations.orphaned) console.warn(`Tables of removed feature "${owner}" remain in the database`);

server.listen(port);
console.log(`Quesshi listening on http://localhost:${port}`);
