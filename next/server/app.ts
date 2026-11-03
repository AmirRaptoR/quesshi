import { Elysia } from "elysia";
import { checkFeatures, featureRoutes, type Core, type Feature, type FeatureContext } from "./core/feature";
import { createAdminAuth } from "./core/admin";
import { adminAuthRoutes } from "./core/admin.routes";
import { createAdminStore } from "./core/admin.store";
import { createAuth, SESSION_DAYS } from "./core/auth";
import { authRoutes, sessionCookie, tokenFromCookies } from "./core/auth.routes";
import { createIdentityStore, type Lang } from "./core/identity.store";
import { CORE_MIGRATIONS, runMigrations } from "./core/migrations";
import { hashPasswordSync } from "./core/passwords";
import { createRooms } from "./core/rooms";
import { createSessionsStore } from "./core/sessions.store";

/** Checks the features, migrates the core then each feature, and mounts every feature's routes. */
export function createApp(core: Core, features: readonly Feature[]) {
  checkFeatures(features);
  const migrations = runMigrations(core.db, [
    { owner: "core", directory: CORE_MIGRATIONS },
    ...features.flatMap((feature) => (feature.migrations ? [{ owner: feature.name, directory: feature.migrations }] : [])),
  ], core.clock);
  const admins = createAdminStore(core.db);
  bootstrapAdmin(core, admins);
  const adminAuth = createAdminAuth({ admins, clock: core.clock, mailer: core.mailer, origin: core.origin });
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
    .use(authRoutes(auth, identity, core.google, () => crypto.randomUUID()))
    .use(adminAuthRoutes(adminAuth));
  const newId = () => crypto.randomUUID();
  const services = {
    ...core,
    playerFor: (header: string | null) => auth.playerFor(tokenFromCookies(header)),
    player: (id: string) => identity.player(id),
    signInGuest(name: string, lang: Lang) {
      const player = auth.createGuest(name, lang, newId);
      return { player, cookie: sessionCookie(auth.openSession(player), SESSION_DAYS * 24 * 3600) };
    },
    publish: (topic: string, message: unknown) => { server.server?.publish(topic, JSON.stringify(message)); },
  };
  const context: FeatureContext = { ...services, rooms: createRooms(services, createSessionsStore(core.db)) };
  for (const feature of features) {
    const routes = featureRoutes(feature, context);
    if (routes) server = server.use(routes);
  }
  return { server, migrations };
}

/** Creates the first administrator when there is none; a generated password is logged and must be changed. */
function bootstrapAdmin(core: Core, admins: ReturnType<typeof createAdminStore>) {
  if (!core.bootstrapAdmin || admins.count() > 0) return;
  const generated = core.bootstrapAdmin.password === null;
  const password = core.bootstrapAdmin.password ?? crypto.randomUUID();
  admins.create({
    id: crypto.randomUUID(), username: core.bootstrapAdmin.username, email: core.bootstrapAdmin.email,
    passwordHash: hashPasswordSync(password), mustChangePassword: generated, createdAt: core.clock.now().toISOString(),
  });
  if (generated) console.warn(`Created the first administrator "${core.bootstrapAdmin.username}" with password ${password}; change it at the first sign-in.`);
}
