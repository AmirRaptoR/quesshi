import { Elysia } from "elysia";
import { checkFeatures, featureRoutes, type Core, type Feature, type FeatureCategories, type FeatureContext } from "./core/feature";
import { createAdminAuth } from "./core/admin";
import { adminAccountRoutes, adminAuthRoutes, adminToken } from "./core/admin.routes";
import { createAdminStore } from "./core/admin.store";
import { createAuth, SESSION_DAYS } from "./core/auth";
import { selectCategories } from "./core/categories";
import { categoryAdminRoutes } from "./core/categories.routes";
import { clientRoutes } from "./core/client.routes";
import { mediaRoutes } from "./core/media.routes";
import { dashboardRoutes } from "./core/dashboard.routes";
import { roomAdminRoutes } from "./core/room-admin.routes";
import { reportAdminRoutes, reportRoutes } from "./core/reports.routes";
import { createReportsStore } from "./core/reports.store";
import { userAdminRoutes } from "./core/user-admin.routes";
import { questionAdminRoutes } from "./core/question-admin.routes";
import { questionSetRoutes } from "./core/question-sets.routes";
import { createQuestionSetsStore } from "./core/question-sets.store";
import { createQuestionsStore, type QuestionsStore } from "./core/questions.store";
import { authRoutes, sessionCookie, tokenFromCookies } from "./core/auth.routes";
import { createIdentityStore, type Lang } from "./core/identity.store";
import { CORE_MIGRATIONS, runMigrations } from "./core/migrations";
import { hashPasswordSync } from "./core/passwords";
import { createRooms } from "./core/rooms";
import { createRoomsStore } from "./core/rooms.store";
import { roomCodeRoutes } from "./core/rooms.routes";
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
  const questions = createQuestionsStore(core.db);
  const reports = createReportsStore(core.db);
  const auth = createAuth({ identity, clock: core.clock, mailer: core.mailer, secret: core.secret });
  const newId = () => crypto.randomUUID();
  const services = {
    ...core,
    playerFor: (header: string | null) => auth.playerFor(tokenFromCookies(header)),
    player: (id: string) => identity.player(id),
    isAdmin: (header: string | null) => adminAuth.adminFor(adminToken(header)) !== null,
    signInGuest(name: string, lang: Lang) {
      const player = auth.createGuest(name, lang, newId);
      return { player, cookie: sessionCookie(auth.openSession(player), SESSION_DAYS * 24 * 3600) };
    },
    publish: (topic: string, message: unknown) => { server.server?.publish(topic, JSON.stringify(message)); },
  };
  const context: Omit<FeatureContext, "categories"> = { ...services, rooms: createRooms(services, createSessionsStore(core.db), createRoomsStore(core.db)) };
  // The server is read only when a message is published, after start-up.
  let server = new Elysia()
    // Every change must come from this site; SameSite cookies plus this check stop cross-site requests.
    .onRequest(({ request, set }) => {
      if (request.method === "GET" || request.method === "HEAD") return;
      if (request.headers.get("origin") !== core.origin) {
        set.status = 403;
        return { error: "cross_site_request" };
      }
    })
    .get("/api/health", () => ({ status: "ok", version: core.version ?? "dev" }))
    .use(authRoutes(auth, identity, core.google, core.telegramBotToken, core.clock, () => crypto.randomUUID()))
    .use(adminAuthRoutes(adminAuth, admins))
    .use(adminAccountRoutes(adminAuth, admins, () => core.clock.now()))
    .use(categoryAdminRoutes(adminAuth, questions, features.map((feature) => feature.name)))
    .use(questionAdminRoutes(adminAuth, questions, features, core.clock))
    .use(userAdminRoutes(adminAuth, identity, core.clock))
    .use(reportRoutes(reports, (cookie) => auth.playerFor(tokenFromCookies(cookie)), core.clock))
    .use(reportAdminRoutes(adminAuth, reports, questions))
    .use(questionSetRoutes(createQuestionSetsStore(core.db), services.playerFor, () => core.clock.now(), newId, new Map(features.flatMap((feature) => Object.entries(feature.kinds ?? {}).map(([kind, rules]) => [kind, { feature: feature.name, rules }] as const)))))
    .use(roomCodeRoutes(context.rooms))
    .use(roomAdminRoutes(adminAuth, context.rooms))
    .use(dashboardRoutes(adminAuth, core.db, context.rooms))
    .use(mediaRoutes(adminAuth, core.mediaDir));
  for (const feature of features) {
    const routes = featureRoutes(feature, { ...context, categories: featureCategories(questions, feature.name, core.random) });
    if (routes) server = server.use(routes);
  }
  if (core.clientDir) server = server.use(clientRoutes(core.clientDir));
  return { server, migrations };
}

function featureCategories(questions: QuestionsStore, feature: string, random: () => number): FeatureCategories {
  return {
    offered: (kinds) => questions.offeredCategories(feature, kinds),
    forGame(picks, lang, kinds) {
      const eligible = questions.offeredCategories(feature, kinds).filter((c) => c.langs.includes(lang)).map((c) => c.id);
      return selectCategories(eligible, picks, random);
    },
  };
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
