import { cpSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import path from "node:path";

/** Builds the client into dist/client: hashed, split bundles plus the static files and index.html. */
const root = path.join(import.meta.dir, "..");
const out = path.join(root, "dist", "client");
const watch = process.argv.includes("--watch");

async function build() {
  rmSync(out, { recursive: true, force: true });
  mkdirSync(out, { recursive: true });
  cpSync(path.join(root, "client", "public"), out, { recursive: true });
  const result = await Bun.build({
    entrypoints: [path.join(root, "client", "main.tsx")],
    outdir: path.join(out, "assets"),
    splitting: true,
    minify: !watch,
    sourcemap: watch ? "inline" : "none",
    naming: { entry: "[name]-[hash].[ext]", chunk: "[name]-[hash].[ext]", asset: "[name]-[hash].[ext]" },
    publicPath: "/assets/",
    // The release, written into the bundle; a build outside a release says "dev".
    define: { "process.env.QUESSHI_VERSION": JSON.stringify(process.env.QUESSHI_VERSION ?? "dev") },
  });
  if (!result.success) {
    for (const message of result.logs) console.error(message);
    throw new Error("The client build failed.");
  }
  const entry = result.outputs.find((output) => output.kind === "entry-point")!;
  const html = readFileSync(path.join(root, "client", "index.html"), "utf8").replace("%MAIN%", `/assets/${path.basename(entry.path)}`);
  writeFileSync(path.join(out, "index.html"), html);
  console.log(`Built the client: ${result.outputs.length} files.`);
}

await build();
if (watch) {
  const { watch: watchFolder } = await import("node:fs");
  let timer: ReturnType<typeof setTimeout> | undefined;
  for (const folder of ["client", "features"]) {
    watchFolder(path.join(root, folder), { recursive: true }, () => {
      clearTimeout(timer);
      timer = setTimeout(() => build().catch((error) => console.error(error)), 100);
    });
  }
}
