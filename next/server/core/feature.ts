import type { AnyElysia } from "elysia";
import type { Clock } from "./clock";
import type { Db } from "./db";

/** What the core hands every feature at start-up. It grows as the core does (auth, events, live hub). */
export interface Core {
  db: Db;
  clock: Clock;
}

/** A self-contained feature: one folder, enabled by being listed in features/index.ts. */
export interface Feature {
  /** Lowercase words joined by hyphens. Also the route prefix (/f/<name>) and the migration owner. */
  name: string;
  /** The feature's folder of forward-only SQL migrations. */
  migrations?: URL;
  /** The feature's HTTP and WebSocket routes, an Elysia instance with the prefix /f/<name>. */
  server?: (core: Core) => AnyElysia;
}

export function defineFeature<const F extends Feature>(feature: F): F {
  return feature;
}

export class FeatureError extends Error {}

const NAME = /^[a-z][a-z0-9]*(-[a-z0-9]+)*$/;

/** Checks the listed features before anything runs. Start-up fails on the first problem. */
export function checkFeatures(features: readonly Feature[]): void {
  const seen = new Set<string>();
  for (const feature of features) {
    if (!NAME.test(feature.name)) throw new FeatureError(`Feature name "${feature.name}" must be lowercase words joined by hyphens.`);
    if (feature.name === "core") throw new FeatureError(`Feature name "core" is reserved.`);
    if (seen.has(feature.name)) throw new FeatureError(`Feature "${feature.name}" is listed twice.`);
    seen.add(feature.name);
  }
}

/** Builds a feature's routes and checks they stay under its own prefix. */
export function featureRoutes(feature: Feature, core: Core): AnyElysia | undefined {
  if (!feature.server) return undefined;
  const routes = feature.server(core);
  const expected = `/f/${feature.name}`;
  if (routes.config.prefix !== expected) {
    throw new FeatureError(`Feature "${feature.name}" must put its routes under the prefix ${expected}, not ${routes.config.prefix ?? "(none)"}.`);
  }
  return routes;
}
