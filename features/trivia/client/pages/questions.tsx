import { useEffect, useState } from "preact/hooks";
import { useLocation } from "preact-iso";
import { useApp } from "../../../../client/app";
import { get, put } from "../../../../client/api";
import { Icon } from "../../../../client/components/icon";
import { LANGS } from "../../../../client/i18n";
import { EASIEST, HARDEST, isWhole, position, withHigh, withLow } from "../logic/difficulty-range";
import { difficultyText, lastSettings, QUESTION_COUNTS, readDraft, remember, summary, toSettings, type Draft } from "../logic/duel-settings";
import { SetPicker, type HostSet } from "../components/set-picker";
import { segment } from "../logic/lobby";
import type { Category } from "../types";

const SCRIPT_SIZE: Record<string, string> = { fa: "1.25rem", en: "1.1rem", nl: "1rem" };

/**
 * Today's questions sheet: language, how many, how hard, on what. Nothing is committed until Done,
 * which remembers the choice and, when the sheet was opened from a lobby its owner is in, also puts
 * it to that lobby. Categories are picked here with today's picker chips.
 */
export function QuestionSettings() {
  const { t } = useApp();
  const { url, route } = useLocation();
  const [draft, setDraft] = useState<Draft>(() => readDraft(url.split("?")[1] ?? "", lastSettings(t.lang)));
  const [categories, setCategories] = useState<Category[] | null>(null);
  const [sets, setSets] = useState<HostSet[] | null>(null);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    // A failed fetch leaves no chips, which reads as "any category"; the rest still works.
    get<Category[]>("/f/trivia/categories").then(setCategories, () => setCategories([]));
    // Guests have no sets and a failed fetch offers none; categories still work.
    get<HostSet[]>("/api/question-sets").then(setSets, () => setSets([]));
  }, []);

  // Picks a language cannot be played in would count with nothing behind them.
  const prune = (next: Draft, list = categories) =>
    list ? { ...next, categoryIds: next.categoryIds.filter((id) => list.some((c) => c.id === id && c.langs.includes(next.lang))) } : next;
  // A remembered set that was deleted since must not be sent.
  useEffect(() => {
    if (sets) setDraft((current) => (current.questionSetId && !sets.some((s) => s.id === current.questionSetId) ? { ...current, questionSetId: null } : current));
  }, [sets]);
  useEffect(() => {
    if (categories) setDraft((current) => prune(current, categories));
  }, [categories]);

  const done = async () => {
    setSaving(true);
    const settings = toSettings(draft);
    remember(settings);
    // A refused change (the room started, or someone else owns it) needs no message: the lobby
    // draws its line from its own view, so it simply still says what is true.
    if (draft.lobby) await put(`/f/trivia/${segment(draft.lobby.format)}/${draft.lobby.id}/settings`, settings).catch(() => null);
    setSaving(false);
    route(draft.returnTo);
  };

  // A set brings its own language; level and category do not apply to it.
  const chooseSet = (id: string | null) => {
    const set = sets?.find((s) => s.id === id);
    setDraft(set ? { ...draft, questionSetId: set.id, lang: set.lang } : { ...draft, questionSetId: null });
  };
  const stacked = draft.levels.low === draft.levels.high && draft.levels.high === HARDEST;
  const toggle = (id: string) =>
    setDraft({ ...draft, categoryIds: draft.categoryIds.includes(id) ? draft.categoryIds.filter((c) => c !== id) : [...draft.categoryIds, id] });

  return (
    <div class="shell">
      <header class="topbar">
        <a class="icon-hit icon-hit--back" href={draft.returnTo} aria-label={t.t("common.back")}><Icon name="chevron-back" size="1.4rem" /></a>
        <h1 class="topbar__title">{t.t("questions.title")}</h1>
      </header>
      {!categories || !sets ? (
        <div class="spinner" role="status" aria-label={t.t("common.loading")} />
      ) : (
        <div class="sheet enter">
          {!draft.questionSetId && (
            <>
          <div class="script-tiles" role="group" aria-label={t.t("lang.pick")}>
            {LANGS.map((code) => (
              <button type="button" class={`script-tile${draft.lang === code ? " is-on" : ""}`} style={`font-size:${SCRIPT_SIZE[code]}`}
                aria-pressed={draft.lang === code} onClick={() => setDraft(prune({ ...draft, lang: code }))}>
                {t.t(`lang.${code}`)}
              </button>
            ))}
          </div>
            </>
          )}

          <div class="count-row" role="group" aria-label={t.t("home.length")}>
            {QUESTION_COUNTS.map((count) => (
              <button type="button" class={`count-row__option${draft.questions === count ? " is-on" : ""}`} aria-pressed={draft.questions === count}
                onClick={() => setDraft({ ...draft, questions: count })}>
                {t.num(count)}
              </button>
            ))}
          </div>

          {!draft.questionSetId && (
            <>
          <div class="ramp">
            <div class="ramp__ends">
              <span>{t.t(`level.${EASIEST}`)}</span>
              {isWhole(draft.levels)
                ? <span class="ramp__ramps">{t.t("questions.rampsUp")}</span>
                : <span class="ramp__span">{difficultyText(summary(toSettings(draft)), t)}</span>}
              <span>{t.t(`level.${HARDEST}`)}</span>
            </div>
            <div class="ramp__track">
              <div class="ramp__rail" />
              <div class="ramp__off ramp__off--low" style={`inline-size:${position(draft.levels.low)}%`} />
              <div class="ramp__off ramp__off--high" style={`inline-size:${100 - position(draft.levels.high)}%`} />
              {/* With both handles on the hardest level, the easy one comes to the front so the pair can be pulled apart. */}
              <input type="range" min={EASIEST} max={HARDEST} step={1} class={`ramp__handle${stacked ? " ramp__handle--front" : ""}`} value={draft.levels.low}
                onInput={(e) => setDraft({ ...draft, levels: withLow(draft.levels, Number(e.currentTarget.value)) })}
                aria-label={t.t("questions.rangeLow")} aria-valuetext={t.t(`level.${draft.levels.low}`)} />
              <input type="range" min={EASIEST} max={HARDEST} step={1} class="ramp__handle" value={draft.levels.high}
                onInput={(e) => setDraft({ ...draft, levels: withHigh(draft.levels, Number(e.currentTarget.value)) })}
                aria-label={t.t("questions.rangeHigh")} aria-valuetext={t.t(`level.${draft.levels.high}`)} />
            </div>
          </div>

          <div class="field" style="margin-top:.9rem">
            <label>{t.t("home.categories")}</label>
            <div class="picker">
              {categories.map((category) => {
                const playable = category.langs.includes(draft.lang);
                const on = draft.categoryIds.includes(category.id);
                return (
                  <button type="button" class={`picker__chip${on ? " is-on" : ""}`} style={`--pick:${category.color}`} disabled={!playable}
                    title={playable ? undefined : t.t("home.noneInLanguage")} aria-pressed={on} onClick={() => toggle(category.id)}>
                    <span aria-hidden="true">{category.icon}</span> {t.name(category)}
                  </button>
                );
              })}
            </div>
            <p class="faint" style="margin:.35rem 0 0;font-size:var(--step--1)">
              {draft.categoryIds.length === 0 ? t.t("home.anyCategory") : t.format("home.pickedCount", draft.categoryIds.length)}
            </p>
          </div>
            </>
          )}

          <SetPicker t={t} sets={sets} value={draft.questionSetId} onChange={chooseSet} />

          <div class="sheet__rest" />
          <button class="btn btn--done" disabled={saving} onClick={() => void done()}>{t.t("questions.done")}</button>
        </div>
      )}
    </div>
  );
}
