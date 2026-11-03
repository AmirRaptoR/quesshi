import { Elysia } from "elysia";
import { checkFeatures, featureRoutes, type Core, type Feature } from "./core/feature";
import { runMigrations } from "./core/migrations";

const CORE_MIGRATIONS = new URL("./core/migrations/", import.meta.url);

/** Checks the features, migrates the core then each feature, and mounts every feature's routes. */
export function createApp(core: Core, features: readonly Feature[]) {
  checkFeatures(features);
  const migrations = runMigrations(core.db, [
    { owner: "core", directory: CORE_MIGRATIONS },
    ...features.flatMap((feature) => (feature.migrations ? [{ owner: feature.name, directory: feature.migrations }] : [])),
  ], core.clock);
  let server = new Elysia().get("/api/health", () => ({ status: "ok" }));
  for (const feature of features) {
    const routes = featureRoutes(feature, core);
    if (routes) server = server.use(routes);
  }
  return { server, migrations };
}
