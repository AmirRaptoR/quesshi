import { useEffect, useRef, useState } from "preact/hooks";
import { loadCountries, radiusInDegrees, VIEW_BOX, type MapCountry, type MapPick } from "../logic/map";

export interface MapMark { pick: MapPick; label: string; right: boolean | null }

const f = (value: number) => String(Math.round(value * 1000) / 1000);

export function useCountries(): MapCountry[] {
  const [countries, setCountries] = useState<MapCountry[]>([]);
  useEffect(() => {
    void loadCountries().then(setCountries);
  }, []);
  return countries;
}

interface Props {
  shape?: "country" | "point";
  baseLayer: "blank" | "borders";
  selection?: MapPick | null;
  onSelect?: (pick: MapPick) => void;
  disabled?: boolean;
  target?: MapPick | null;
  toleranceKm?: number | null;
  marks?: MapMark[];
  label: string;
}

/**
 * The world, inline: one path per country from maps/world.svg, so a tap on a country is a click on
 * the element the browser already decided was hit. No pan and no zoom, as today.
 */
export function WorldMap({ shape = "country", baseLayer, selection = null, onSelect, disabled = false, target = null, toleranceKm = null, marks = [], label }: Props) {
  const countries = useCountries();
  const svg = useRef<SVGSVGElement>(null);
  const interactive = Boolean(onSelect) && !disabled;
  const pointPicking = interactive && shape === "point";
  const points = [...marks.filter((m) => m.pick.shape === "point"), ...(selection?.shape === "point" ? [{ pick: selection, label: "", right: null }] : [])];
  const at = (pick: MapPick | null) => (pick?.shape === "point" ? { x: pick.longitude, y: -pick.latitude, lat: pick.latitude } : null);
  const targetPoint = at(target);
  const ringPoint = targetPoint ?? at(selection);

  const toneOf = (iso: string) => {
    if (target?.shape === "country" && target.countryCode === iso) return " is-target";
    if (selection?.shape === "country" && selection.countryCode === iso) return " is-picked";
    const mark = marks.find((m) => m.pick.shape === "country" && m.pick.countryCode === iso);
    return mark ? (mark.right === false ? " is-miss" : " is-picked") : "";
  };
  const clickMap = (event: MouseEvent) => {
    if (!pointPicking || !svg.current) return;
    // The browser's own screen-to-viewBox matrix stays right at any size or pixel ratio.
    const matrix = svg.current.getScreenCTM();
    if (!matrix) return;
    const point = new DOMPoint(event.clientX, event.clientY).matrixTransform(matrix.inverse());
    onSelect?.({ shape: "point", latitude: -point.y, longitude: point.x });
  };

  return (
    <svg class={`worldmap${baseLayer === "borders" ? " worldmap--borders" : ""}${interactive ? " worldmap--pick" : ""}`} viewBox={VIEW_BOX}
      preserveAspectRatio="xMidYMid meet" ref={svg} role="img" aria-label={label} onClick={clickMap}>
      {/* Transparent rather than none, so the sea answers a tap the way land does. */}
      {pointPicking && <rect class="worldmap__sea" x="-180" y="-90" width="360" height="180" />}
      <g fill-rule="evenodd">
        {countries.map((country) => (
          <path class={`worldmap__country${toneOf(country.iso)}`} d={country.d} key={country.iso}
            onClick={() => { if (interactive && shape === "country") onSelect?.({ shape: "country", countryCode: country.iso }); }} />
        ))}
      </g>
      {ringPoint && toleranceKm !== null && (() => {
        const [rx, ry] = radiusInDegrees(toleranceKm, ringPoint.lat);
        return <ellipse class="worldmap__tolerance" cx={f(ringPoint.x)} cy={f(ringPoint.y)} rx={f(rx)} ry={f(ry)} />;
      })()}
      {targetPoint && (
        <>
          {points.filter((m) => m.right === false).map((m) => {
            const p = at(m.pick)!;
            return <line class="worldmap__miss" x1={f(p.x)} y1={f(p.y)} x2={f(targetPoint.x)} y2={f(targetPoint.y)} />;
          })}
          <circle class="worldmap__pin worldmap__pin--target" cx={f(targetPoint.x)} cy={f(targetPoint.y)} r="8" />
        </>
      )}
      {points.map((m) => {
        const p = at(m.pick)!;
        return (
          <>
            <circle class="worldmap__pin" style={m.right === false ? "fill:var(--anaar)" : undefined} cx={f(p.x)} cy={f(p.y)} r="5" />
            <line class="worldmap__cross" x1={f(p.x - 11)} y1={f(p.y)} x2={f(p.x + 11)} y2={f(p.y)} />
            <line class="worldmap__cross" x1={f(p.x)} y1={f(p.y - 11)} x2={f(p.x)} y2={f(p.y + 11)} />
          </>
        );
      })}
    </svg>
  );
}
