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
const { server, migrations } = createApp({ db, clock: systemClock }, features);

for (const name of migrations.applied) console.log(`Applied migration ${name}`);
for (const owner of migrations.orphaned) console.warn(`Tables of removed feature "${owner}" remain in the database`);

server.listen(port);
console.log(`Quesshi listening on http://localhost:${port}`);
