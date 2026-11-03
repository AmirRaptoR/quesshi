import { describe, expect, test } from "bun:test";
import { MalformedAnswer } from "../../../server/core/kind";
import { mapPick, type MapPickPayload } from "./map-pick";

const amsterdam = { latitude: 52.3676, longitude: 4.9041 };
const london = { latitude: 51.5074, longitude: -0.1278 }; // about 357 km from Amsterdam

const pointNear = (radiusKm: number): MapPickPayload => ({ baseLayer: "borders", target: { shape: "point", ...amsterdam, radiusKm } });
const country: MapPickPayload = { baseLayer: "blank", target: { shape: "country", countryCode: "NL" } };

const play = (payload: MapPickPayload, answer: unknown) => {
  const resolved = mapPick.resolve(payload, Math.random);
  return mapPick.grade(payload, resolved, mapPick.normalize(payload, resolved, answer));
};

describe("map_pick", () => {
  test("the card shows the base layer and what to pick, never the target", () => {
    const elsewhere: MapPickPayload = { baseLayer: "blank", target: { shape: "country", countryCode: "FR" } };

    expect(mapPick.card(country, mapPick.resolve(country, Math.random))).toEqual({ baseLayer: "blank", shape: "country" });
    expect(mapPick.card(elsewhere, mapPick.resolve(elsewhere, Math.random))).toEqual(mapPick.card(country, mapPick.resolve(country, Math.random)));
  });

  test("the reveal is the target", () => {
    expect(mapPick.reveal(country, mapPick.resolve(country, Math.random))).toEqual({ target: country.target });
  });

  test("a country answer earns credit only for the target country", () => {
    expect(play(country, { shape: "country", countryCode: "NL" })).toBe(1);
    expect(play(country, { shape: "country", countryCode: "BE" })).toBe(0);
  });

  test("a point answer earns credit within the radius, by great-circle distance", () => {
    expect(play(pointNear(360), { shape: "point", ...london })).toBe(1);
    expect(play(pointNear(350), { shape: "point", ...london })).toBe(0);
  });

  test("a point exactly on the radius earns credit", () => {
    expect(play(pointNear(10), { shape: "point", latitude: amsterdam.latitude + (10 / 6371.0088) * (180 / Math.PI), longitude: amsterdam.longitude })).toBe(1);
  });

  test("refuses answers no client could send", () => {
    const point = pointNear(100);
    for (const answer of [
      undefined,
      { shape: "point", countryCode: "NL" },
      { shape: "point", latitude: 91, longitude: 0 },
      { shape: "point", latitude: 0, longitude: 181 },
      { shape: "point", latitude: "52", longitude: 4 },
      { shape: "country", countryCode: "NL" },
    ]) {
      expect(() => play(point, answer)).toThrow(MalformedAnswer);
    }
    for (const answer of [{ shape: "country", countryCode: "nl" }, { shape: "country", countryCode: "NLD" }, { shape: "point", ...amsterdam }]) {
      expect(() => play(country, answer)).toThrow(MalformedAnswer);
    }
  });
});
