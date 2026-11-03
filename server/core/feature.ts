import type { AnyElysia } from "elysia";
import type { Clock } from "./clock";
import type { Db } from "./db";
import type { Lang, Player } from "./identity.store";
import type { KindRules } from "./kind";
import type { Mailer } from "./mailer";
import type { Category } from "./questions.store";
import type { Rooms } from "./rooms";
import type { Schedule } from "./schedule";

/** What the core hands every feature at start-up. It grows as the core does (events, live hub). */
export interface Core {
  db: Db;
  clock: Clock;
  schedule: Schedule;
  random: () => number;
  mailer: Mailer;
  /** Signs sign-in codes; set from the environment in production. */
  secret: string;
  /** The running release, reported by the health route. */
  version?: string;
  /** The built client to serve, when there is one. */
  clientDir?: string;
  /** Where uploaded media files are kept. */
  mediaDir: string;
  /** The site's own origin; changes requested from any other origin are refused. */
  origin: string;
  /** The first administrator, created when there is none; a null password is generated and must be changed. */
  bootstrapAdmin?: { username: string; email: string; password: string | null };
  /** Google sign-in, when a client id is configured. verify returns the verified email and name. */
  google?: { clientId: string; verify(idToken: string): Promise<{ email: string; name: string | null } | null> };
}

/** The core's services a feature uses at run time, on top of the Core settings. */
export interface FeatureContext extends Core {
  /** The signed-in player for a request's cookie header, or null. */
  playerFor(cookieHeader: string | null): Player | null;
  player(id: string): Player | null;
  /** Creates a guest player and returns them with the cookie that signs them in. */
  signInGuest(name: string, lang: Lang): { player: Player; cookie: string };
  /** Whether a request's cookie header belongs to a signed-in administrator. */
  isAdmin(cookieHeader: string | null): boolean;
  /** Sends a message to every WebSocket subscribed to the topic. */
  publish(topic: string, message: unknown): void;
  /** Every feature's game lobbies, sharing one set of codes. */
  rooms: Rooms;
  /** The categories an admin set this feature to offer. */
  categories: FeatureCategories;
}

export interface FeatureCategories {
  /** The active offered categories, each with the languages that have approved questions of these kinds. */
  offered(kinds: readonly string[]): (Category & { langs: string[] })[];
  /** A game's categories, by today's rule, among the offered ones with approved questions of these kinds in the language. */
  forGame(picks: readonly string[], lang: string, kinds: readonly string[]): string[];
}

/** A self-contained feature: one folder, enabled by being listed in features/index.ts. */
export interface Feature {
  /** Lowercase words joined by hyphens. Also the route prefix (/f/<name>) and the migration owner. */
  name: string;
  /** The feature's folder of forward-only SQL migrations. */
  migrations?: URL;
  /** The question kinds the feature plays, by id; each id belongs to one feature. */
  kinds?: Readonly<Record<string, KindRules>>;
  /** The feature's HTTP and WebSocket routes, an Elysia instance with the prefix /f/<name>. */
  server?: (context: FeatureContext) => AnyElysia;
}

export function defineFeature<const F extends Feature>(feature: F): F {
  return feature;
}

export class FeatureError extends Error {}

const NAME = /^[a-z][a-z0-9]*(-[a-z0-9]+)*$/;

/** Checks the listed features before anything runs. Start-up fails on the first problem. */
export function checkFeatures(features: readonly Feature[]): void {
  const seen = new Set<string>();
  const kinds = new Map<string, string>();
  for (const feature of features) {
    for (const kind of Object.keys(feature.kinds ?? {})) {
      const owner = kinds.get(kind);
      if (owner) throw new FeatureError(`Question kind "${kind}" belongs to both "${owner}" and "${feature.name}".`);
      kinds.set(kind, feature.name);
    }
    if (!NAME.test(feature.name)) throw new FeatureError(`Feature name "${feature.name}" must be lowercase words joined by hyphens.`);
    if (feature.name === "core") throw new FeatureError(`Feature name "core" is reserved.`);
    if (seen.has(feature.name)) throw new FeatureError(`Feature "${feature.name}" is listed twice.`);
    seen.add(feature.name);
  }
}

/** Builds a feature's routes and checks they stay under its own prefix. */
export function featureRoutes(feature: Feature, context: FeatureContext): AnyElysia | undefined {
  if (!feature.server) return undefined;
  const routes = feature.server(context);
  const expected = `/f/${feature.name}`;
  if (routes.config.prefix !== expected) {
    throw new FeatureError(`Feature "${feature.name}" must put its routes under the prefix ${expected}, not ${routes.config.prefix ?? "(none)"}.`);
  }
  return routes;
}
