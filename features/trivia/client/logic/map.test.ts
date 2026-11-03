import { describe, expect, test } from "bun:test";
import { coordinates, countryName, parseCountries, radiusInDegrees } from "./map";

const svg = `<svg><path id="NL" class="c" d="M1 2Z"><title>Netherlands</title></path><path id="BE" d="M3 4Z"/><g id="x"/></svg>`;

describe("the world map", () => {
  test("reads one country per two-letter path, with its name when it has one", () => {
    expect(parseCountries(svg)).toEqual([{ iso: "NL", name: "Netherlands", d: "M1 2Z" }, { iso: "BE", name: "", d: "M3 4Z" }]);
  });

  test("names a country by its code, falling back to the code", () => {
    const countries = parseCountries(svg);
    expect(countryName(countries, "NL")).toBe("Netherlands");
    expect(countryName(countries, "BE")).toBe("BE");
  });

  test("writes a point as degrees with at most two decimals, in invariant digits", () => {
    expect(coordinates({ shape: "point", latitude: 52.3676, longitude: -4.9 })).toBe("52.37°, -4.9°");
  });

  test("draws a radius as an ellipse that widens towards the poles", () => {
    const [rx, ry] = radiusInDegrees(111.19508, 0);
    expect(ry).toBeCloseTo(1, 3);
    expect(rx).toBeCloseTo(1, 3);
    expect(radiusInDegrees(111.19508, 60)[0]).toBeCloseTo(2, 3);
  });
});
