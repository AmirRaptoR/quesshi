import { useApp } from "../../../../client/app";
import { coordinates, countryName, type MapPick } from "../logic/map";
import { useCountries, WorldMap } from "./world-map";

interface Props {
  shape: "country" | "point";
  baseLayer: "blank" | "borders";
  selection: MapPick | null;
  onSelect(pick: MapPick): void;
  toleranceKm?: number | null;
  disabled?: boolean;
  onConfirm?: () => void;
}

/** A map question as it is answered, and how an admin sets a target: the world and a bar naming the last tap. */
export function MapField({ shape, baseLayer, selection, onSelect, toleranceKm = null, disabled = false, onConfirm }: Props) {
  const { t } = useApp();
  const countries = useCountries();
  return (
    <>
      <WorldMap shape={shape} baseLayer={baseLayer} disabled={disabled} toleranceKm={toleranceKm} selection={selection} onSelect={onSelect} label={t.t("map.label")} />
      <div class={`mapbar${selection ? "" : " mapbar--empty"}`} role="status">
        {!selection ? (
          <span class="grow">{t.t(shape === "point" ? "map.tapPoint" : "map.tapCountry")}</span>
        ) : selection.shape === "country" ? (
          <>
            <span class="mapbar__mark" aria-hidden="true" />
            <span class="mapbar__name grow" dir="ltr">{countryName(countries, selection.countryCode)}</span>
            <span class="chip">{selection.countryCode}</span>
          </>
        ) : (
          <>
            <span class="mapbar__mark" aria-hidden="true" />
            <span class="mapbar__coords grow" dir="ltr">{t.num(coordinates(selection))}</span>
          </>
        )}
        {onConfirm && <button class="btn btn--sm" disabled={!selection || disabled} onClick={onConfirm}>{t.t("play.lockIn")}</button>}
      </div>
    </>
  );
}
