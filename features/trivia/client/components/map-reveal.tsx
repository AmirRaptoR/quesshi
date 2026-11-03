import { useApp } from "../../../../client/app";
import { coordinates, countryName, type MapPick } from "../logic/map";
import type { MapTarget } from "../types";
import { useCountries, WorldMap, type MapMark } from "./world-map";

export interface MapAnswer { label: string; pick: MapPick | null; right: boolean | null; answered: boolean }

/**
 * A map question's answer: the target and every pick on one map, with a legend naming them. Borders
 * by default even for a question played blank: once answered, they explain where you were.
 */
export function MapReveal({ target, answers, baseLayer = "borders" }: { target: MapTarget; answers: MapAnswer[]; baseLayer?: "blank" | "borders" }) {
  const { t } = useApp();
  const countries = useCountries();
  const pick: MapPick = target.shape === "country" ? target : { shape: "point", latitude: target.latitude, longitude: target.longitude };
  const marks: MapMark[] = answers.flatMap((a) => (a.answered && a.pick ? [{ pick: a.pick, label: a.label, right: a.right }] : []));
  const describe = (p: MapPick) => (p.shape === "country" ? `${countryName(countries, p.countryCode)} (${p.countryCode})` : t.num(coordinates(p)));
  return (
    <>
      <WorldMap baseLayer={baseLayer} target={pick} marks={marks} toleranceKm={target.shape === "point" ? target.radiusKm : null} label={t.t("map.label")} />
      <div class="maplegend">
        <div class="maplegend__row">
          <span class="mapbar__mark mapbar__mark--target" aria-hidden="true" />
          <span dir="auto">{t.t("map.answer")}</span>
          <span class="grow" dir="ltr">{describe(pick)}</span>
        </div>
        {marks.map((mark, i) => (
          <div class="maplegend__row" key={i}>
            <span class={`mapbar__mark${mark.right === false ? " mapbar__mark--miss" : ""}`} aria-hidden="true" />
            <span dir="auto">{mark.label}</span>
            <span class="grow" dir="ltr">{describe(mark.pick)}</span>
          </div>
        ))}
        {answers.filter((a) => !a.answered).map((a) => (
          <div class="maplegend__row" key={a.label}>
            <span dir="auto">{a.label}</span>
            <span class="grow">{t.t("live.noAnswer")}</span>
          </div>
        ))}
      </div>
    </>
  );
}
