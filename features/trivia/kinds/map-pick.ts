import { MalformedAnswer, numberCell, refuse, type Kind } from "../../../server/core/kind";

export type MapTarget =
  | { shape: "country"; countryCode: string }
  | { shape: "point"; latitude: number; longitude: number; radiusKm: number };
export interface MapPickPayload { baseLayer: "blank" | "borders"; target: MapTarget }
export interface MapPickCard { baseLayer: MapPickPayload["baseLayer"]; shape: MapTarget["shape"] }
export type MapPickAnswer = { shape: "country"; countryCode: string } | { shape: "point"; latitude: number; longitude: number };
export interface MapPickReveal { target: MapTarget }

const EARTH_RADIUS_KM = 6371.0088;
/** Below 10 km a target is smaller than a fingertip on a world map; above 2000 km a continent is "correct". */
const MIN_RADIUS_KM = 10;
const MAX_RADIUS_KM = 2000;
const finiteIn = (value: unknown, limit: number) => typeof value === "number" && Number.isFinite(value) && Math.abs(value) <= limit;

/** Pick a country, or a point within a radius, on a world map. */
export const mapPick: Kind<MapPickPayload, null, MapPickCard, MapPickAnswer, MapPickReveal> = {
  id: "map_pick",
  levelled: true,
  columns: ["targetshape", "countrycode", "latitude", "longitude", "radiuskm", "baselayer"],
  // Today's files call a point target a city.
  fromRow: (row) => ({
    baseLayer: row.baselayer,
    target: row.targetshape === "city"
      ? { shape: "point", latitude: numberCell(row.latitude), longitude: numberCell(row.longitude), radiusKm: numberCell(row.radiuskm) }
      : { shape: row.targetshape, countryCode: row.countrycode },
  }),
  example: { prompt: "Which country is shaped like this?", targetshape: "country", countrycode: "NL", latitude: "", longitude: "", radiuskm: "", baselayer: "borders" },
  check(payload) {
    const { baseLayer, target } = (payload ?? {}) as Record<string, unknown>;
    if (baseLayer !== "blank" && baseLayer !== "borders") return refuse("bad_base_layer");
    const t = (target ?? {}) as Record<string, unknown>;
    if (t.shape === "country") {
      // Whether the map can draw the country is checked when the map itself is part of the app.
      if (typeof t.countryCode !== "string" || !/^[A-Z]{2}$/.test(t.countryCode)) return refuse("bad_country_code");
      return { ok: true, payload: { baseLayer, target: { shape: "country", countryCode: t.countryCode } } };
    }
    if (t.shape !== "point") return refuse("bad_target_shape");
    if (!finiteIn(t.latitude, 90)) return refuse("bad_latitude");
    if (!finiteIn(t.longitude, 180)) return refuse("bad_longitude");
    if (typeof t.radiusKm !== "number" || !(t.radiusKm >= MIN_RADIUS_KM && t.radiusKm <= MAX_RADIUS_KM)) return refuse("bad_radius");
    return { ok: true, payload: { baseLayer, target: { shape: "point", latitude: t.latitude as number, longitude: t.longitude as number, radiusKm: t.radiusKm } } };
  },
  resolve: () => null,
  card: (payload) => ({ baseLayer: payload.baseLayer, shape: payload.target.shape }),
  reveal: (payload) => ({ target: payload.target }),
  normalize(payload, _resolved, answer) {
    const given = (answer ?? {}) as Record<string, unknown>;
    if (payload.target.shape === "country") {
      if (given.shape === "country" && typeof given.countryCode === "string" && /^[A-Z]{2}$/.test(given.countryCode)) {
        return { shape: "country", countryCode: given.countryCode };
      }
      throw new MalformedAnswer("A country answer must be one uppercase two-letter country code.");
    }
    const { latitude, longitude } = given;
    if (given.shape === "point" && isCoordinate(latitude, 90) && isCoordinate(longitude, 180)) {
      return { shape: "point", latitude, longitude };
    }
    throw new MalformedAnswer("A point answer must be a latitude and longitude in range.");
  },
  grade(payload, _resolved, stored) {
    const target = payload.target;
    if (target.shape === "country") return stored.shape === "country" && stored.countryCode === target.countryCode ? 1 : 0;
    if (stored.shape !== "point") return 0;
    return distanceKm(target.latitude, target.longitude, stored.latitude, stored.longitude) <= target.radiusKm ? 1 : 0;
  },
};

function isCoordinate(value: unknown, limit: number): value is number {
  return typeof value === "number" && Number.isFinite(value) && Math.abs(value) <= limit;
}

/** Great-circle (haversine) distance, with the same mean Earth radius as today's application. */
function distanceKm(lat1: number, lon1: number, lat2: number, lon2: number): number {
  const rad = Math.PI / 180;
  const h = Math.sin(((lat2 - lat1) * rad) / 2) ** 2
    + Math.cos(lat1 * rad) * Math.cos(lat2 * rad) * Math.sin(((lon2 - lon1) * rad) / 2) ** 2;
  return 2 * EARTH_RADIUS_KM * Math.asin(Math.min(1, Math.sqrt(h)));
}
