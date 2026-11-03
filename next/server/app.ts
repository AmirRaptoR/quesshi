import { Elysia } from "elysia";
import { checkFeatures, featureRoutes, type Core, type Feature } from "./core/feature";
import { createAuth } from "./core/auth";
import { authRoutes } from "./core/auth.routes";
import { createIdentityStore } from "./core/identity.store";
import { CORE_MIGRATIONS, runMigrations } from "./core/migrations";

/** Checks the features, migrates the core then each feature, and mounts every feature's routes. */
export function createApp(core: Core, features: readonly Feature[]) {
  checkFeatures(features);
  const migrations = runMigrations(core.db, [
    { owner: "core", directory: CORE_MIGRATIONS },
    ...features.flatMap((feature) => (feature.migrations ? [{ owner: feature.name, directory: feature.migrations }] : [])),
  ], core.clock);
  const identity = createIdentityStore(core.db);
  const auth = createAuth({ identity, clock: core.clock, mailer: core.mailer, secret: core.secret });
  let server = new Elysia()
    // Every change must come from this site; SameSite cookies plus this check stop cross-site requests.
    .onRequest(({ request, set }) => {
      if (request.method === "GET" || request.method === "HEAD") return;
      if (request.headers.get("origin") !== core.origin) {
        set.status = 403;
        return { error: "cross_site_request" };
      }
    })
    .get("/api/health", () => ({ status: "ok" }))
    .use(authRoutes(auth, identity, core.google, () => crypto.randomUUID()));
  for (const feature of features) {
    const routes = featureRoutes(feature, core);
    if (routes) server = server.use(routes);
  }
  return { server, migrations };
}
