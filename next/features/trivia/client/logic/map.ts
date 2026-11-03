/** A pick on the world map, in the map_pick kind's answer shape. */
export type MapPick = { shape: "country"; countryCode: string } | { shape: "point"; latitude: number; longitude: number };
export interface MapCountry { iso: string; name: string; d: string }

/** Equirectangular: x is longitude, y is negated latitude. */
export const VIEW_BOX = "-180 -90 360 180";
const EARTH_RADIUS_KM = 6371.0088;
const KM_PER_DEGREE = (Math.PI * EARTH_RADIUS_KM) / 180;

export function parseCountries(svg: string): MapCountry[] {
  return [...svg.matchAll(/<path id="([A-Z]{2})"[^>]*\sd="([^"]+)"[^>]*>(?:<title>([^<]*)<\/title>)?/g)].map((m) => ({ iso: m[1]!, d: m[2]!, name: m[3] ?? "" }));
}

let loading: Promise<MapCountry[]> | null = null;
/** The map's countries, fetched once; a failed fetch is forgotten so a later question tries again. */
export function loadCountries(): Promise<MapCountry[]> {
  loading ??= fetch("/maps/world.svg").then((r) => r.text()).then(parseCountries).catch(() => {
    loading = null;
    return [];
  });
  return loading;
}

export const countryName = (countries: readonly MapCountry[], iso: string) => countries.find((c) => c.iso === iso)?.name || iso;

const degrees = (value: number) => `${Math.round(value * 100) / 100}°`;
export const coordinates = (pick: MapPick) => (pick.shape === "point" ? `${degrees(pick.latitude)}, ${degrees(pick.longitude)}` : "");

/** A radius on the ground as the ellipse it draws on this projection. */
export function radiusInDegrees(radiusKm: number, latitude: number): [number, number] {
  const ry = radiusKm / KM_PER_DEGREE;
  const cosine = Math.max(Math.cos((latitude * Math.PI) / 180), 1e-6);
  return [Math.min(ry / cosine, 180), ry];
}
