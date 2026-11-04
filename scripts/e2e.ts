/** Builds the client, then runs the browser suite; it starts its own apps on free ports with temporary data. */
const run = (command: string[]) => Bun.spawnSync(command, { stdio: ["inherit", "inherit", "inherit"] }).exitCode;

if (run(["bun", "run", "build"]) !== 0) process.exit(1);
process.exit(run(["bun", "x", "playwright", "test", ...process.argv.slice(2)]));
