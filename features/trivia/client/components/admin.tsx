import { useEffect, useState } from "preact/hooks";
import { useApp } from "../../../../client/app";
import { get, put } from "../../../../client/api";
import type { PayloadFormProps } from "../../../../client/feature";
import type { MapPick } from "../logic/map";
import type { MapTarget } from "../types";
import { KEYS } from "./game";
import { MapField } from "./map-field";

type Record_ = Record<string, unknown>;
const asRecord = (value: unknown): Record_ => (value && typeof value === "object" ? (value as Record_) : {});
const four = (value: unknown, pick: (item: unknown) => string) => [0, 1, 2, 3].map((i) => (Array.isArray(value) ? pick(value[i]) : ""));

/** Today's choice editor: four options, the correct one marked by its radio. */
export function ChoiceForm({ payload, onChange }: PayloadFormProps) {
  const { t } = useApp();
  const { options, correctIndex } = asRecord(payload);
  const texts = four(options, (o) => (typeof o === "string" ? o : ""));
  const correct = typeof correctIndex === "number" ? correctIndex : 0;
  const change = (next: { options?: string[]; correctIndex?: number }) => onChange({ options: texts, correctIndex: correct, ...next });
  return (
    <div class="field">
      <label>{t.t("admin.choices")} · {t.t("admin.correct")}</label>
      {texts.map((text, index) => (
        <div class="row" style="margin-bottom:.4rem">
          <input type="radio" name="correct" checked={correct === index} onChange={() => change({ correctIndex: index })} aria-label={`Correct: ${index + 1}`} />
          <input class="input grow" value={text} onInput={(e) => change({ options: texts.map((old, i) => (i === index ? e.currentTarget.value : old)) })} />
        </div>
      ))}
    </div>
  );
}

/** Today's sorting editor: four items written in their correct order, numbered as the play screen numbers them. */
export function OrderForm({ payload, onChange }: PayloadFormProps) {
  const { t } = useApp();
  const texts = four(asRecord(payload).items, (item) => {
    const text = asRecord(item).text;
    return typeof text === "string" ? text : "";
  });
  // The ids only tell the items apart; the place an item is written in is the answer.
  const change = (index: number, value: string) => onChange({ items: texts.map((old, i) => ({ id: String(i + 1), text: i === index ? value : old })) });
  return (
    <div class="field">
      <label>{t.t("admin.items")}</label>
      {texts.map((text, index) => (
        <div class="row" style="margin-bottom:.4rem">
          <span class="sortrow__place">{t.num(index + 1)}</span>
          <input class="input grow" value={text} onInput={(e) => change(index, e.currentTarget.value)} />
        </div>
      ))}
      <p class="faint" style="margin:.35rem 0 0;font-size:var(--step--1)">{t.t("admin.sortOrderHint")}</p>
    </div>
  );
}

const MIN_KM = 10;
const MAX_KM = 2000;
const DEFAULT_KM = 150;

/** Today's map editor: the author sets the target the way a player picks one, on the same map. */
export function MapPickForm({ payload, onChange }: PayloadFormProps) {
  const { t } = useApp();
  const raw = asRecord(payload);
  const target = asRecord(raw.target);
  const baseLayer = raw.baseLayer === "blank" ? "blank" : "borders";
  const shape = target.shape === "point" ? "point" : "country";
  const radiusKm = typeof target.radiusKm === "number" ? target.radiusKm : DEFAULT_KM;
  const pick: MapPick | null = shape === "country"
    ? typeof target.countryCode === "string" ? { shape: "country", countryCode: target.countryCode } : null
    : typeof target.latitude === "number" && typeof target.longitude === "number" ? { shape: "point", latitude: target.latitude, longitude: target.longitude } : null;
  const save = (next: { baseLayer?: string; target?: Record_ }) => onChange({ baseLayer, target, ...next });
  const pickTarget = (picked: MapPick) =>
    save({ target: picked.shape === "country" ? picked : { ...picked, latitude: round(picked.latitude), longitude: round(picked.longitude), radiusKm } });

  return (
    <>
      <div class="field">
        <label id="shape-label">{t.t("admin.target")}</label>
        <div class="segmented" role="radiogroup" aria-labelledby="shape-label">
          {(["country", "point"] as const).map((value) => (
            <button type="button" class={`segmented__option${shape === value ? " is-on" : ""}`} role="radio" aria-checked={shape === value}
              onClick={() => save({ target: value === "country" ? { shape: "country" } : { shape: "point", radiusKm } })}>
              {t.t(`admin.target.${value === "point" ? "city" : "country"}`)}
            </button>
          ))}
        </div>
      </div>
      <div class="field">
        <label>{t.t("admin.baseLayer")}</label>
        <select class="select" style="width:auto" value={baseLayer} onChange={(e) => save({ baseLayer: e.currentTarget.value })}>
          <option value="borders">{t.t("admin.baseLayer.borders")}</option>
          <option value="blank">{t.t("admin.baseLayer.blank")}</option>
        </select>
      </div>
      <MapField shape={shape} baseLayer={baseLayer} selection={pick} onSelect={pickTarget} toleranceKm={shape === "point" ? radiusKm : null} />
      {shape === "point" && (
        // The ring on the map redraws as this moves, the only way to judge a radius.
        <div class="field">
          <label for="radius">{t.t("admin.radius")}</label>
          <div class="row" style="gap:.75rem">
            <input id="radius" class="grow" type="range" step={10} min={MIN_KM} max={MAX_KM} value={radiusKm}
              onInput={(e) => save({ target: { ...target, radiusKm: Number(e.currentTarget.value) } })} />
            <span class="chip" dir="ltr">{t.num(Math.round(radiusKm))} {t.t("admin.km")}</span>
          </div>
        </div>
      )}
    </>
  );
}

const round = (value: number) => Math.round(value * 1e4) / 1e4;

/** The options of a choice or sorting question, as the admin's question card lists them. */
function OptionsPreview({ texts, correct, numbered }: { texts: string[]; correct: number | null; numbered: boolean }) {
  const { t } = useApp();
  return (
    <div class="admin-options" dir="auto">
      {texts.map((text, i) => (
        <div class={`admin-option${i === correct ? " is-correct" : ""}`}>
          <span class="admin-option__key">{numbered ? t.num(i + 1) : KEYS[i]}</span>
          <span>{text}</span>
        </div>
      ))}
    </div>
  );
}

export function ChoicePreview({ payload }: { payload: unknown }) {
  const { options, correctIndex } = asRecord(payload);
  return <OptionsPreview texts={four(options, (o) => String(o ?? ""))} correct={typeof correctIndex === "number" ? correctIndex : null} numbered={false} />;
}

export function OrderPreview({ payload }: { payload: unknown }) {
  return <OptionsPreview texts={four(asRecord(payload).items, (item) => String(asRecord(item).text ?? ""))} correct={null} numbered />;
}

export function MapPickPreview({ payload }: { payload: unknown }) {
  const { t } = useApp();
  const target = asRecord(payload).target as MapTarget | undefined;
  const label = target?.shape === "point" ? `${target.latitude},${target.longitude} · ${Math.round(target.radiusKm)} ${t.t("admin.km")}` : target?.countryCode ?? "—";
  return (
    <div class="admin-options" dir="auto">
      <div class="admin-option is-correct"><span class="admin-option__key">✓</span><span dir="ltr">{label}</span></div>
    </div>
  );
}

interface TriviaSettings { liveEnabled: boolean; maxCapacity: number }

function useTriviaSettings() {
  const [settings, setSettings] = useState<TriviaSettings | null>(null);
  useEffect(() => {
    get<TriviaSettings>("/f/trivia/admin/settings").then(setSettings, () => {});
  }, []);
  const save = async (changes: Partial<TriviaSettings>) => setSettings(await put<TriviaSettings>("/f/trivia/admin/settings", changes));
  return { settings, save };
}

/** Today's live switch: off stops new lobbies and joins; games already playing finish. */
export function LiveSwitchPanel() {
  const { t } = useApp();
  const { settings, save } = useTriviaSettings();
  const [toggling, setToggling] = useState(false);
  if (!settings) return null;
  const toggle = async () => {
    setToggling(true);
    await save({ liveEnabled: !settings.liveEnabled }).catch(() => {});
    setToggling(false);
  };
  return (
    <section class="card enter" style="margin-top:1rem">
      <div class="row row--between">
        <span class="eyebrow">{t.t("admin.live")}</span>
        <label class="row" style="gap:.4rem">
          <span class="faint" style="font-size:var(--step--1)">{t.t(settings.liveEnabled ? "admin.live.enabled" : "admin.live.disabledLabel")}</span>
          <input type="checkbox" checked={settings.liveEnabled} disabled={toggling} onChange={() => void toggle()} />
        </label>
      </div>
      {!settings.liveEnabled && <div class="notice" style="margin-top:.8rem">{t.t("admin.live.offNotice")}</div>}
    </section>
  );
}

/** Today's largest-lobby setting, within the server's bounds of two to five hundred. */
export function LobbyCapacityPanel() {
  const { t } = useApp();
  const { settings, save } = useTriviaSettings();
  const [draft, setDraft] = useState<number | null>(null);
  const [saving, setSaving] = useState(false);
  if (!settings) return null;
  const value = draft ?? settings.maxCapacity;
  const submit = async () => {
    if (!Number.isInteger(value) || value < 2 || value > 500) return;
    setSaving(true);
    await save({ maxCapacity: value }).catch(() => {});
    setDraft(null);
    setSaving(false);
  };
  return (
    <section class="card enter" style="margin-top:1rem">
      <span class="eyebrow">{t.t("admin.lobbyCapacity")}</span>
      <div class="row" style="gap:.5rem;margin-top:.8rem">
        <input class="input" style="width:8rem" type="number" min={2} max={500} value={value} onInput={(e) => setDraft(Number(e.currentTarget.value))} />
        <button class="btn btn--sm" disabled={saving} onClick={() => void submit()}>{t.t("admin.save")}</button>
      </div>
    </section>
  );
}
