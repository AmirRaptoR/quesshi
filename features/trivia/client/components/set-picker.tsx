import type { Translator } from "../../../../client/i18n";

/** One of the host's question sets, as GET /api/question-sets lists it. */
export interface HostSet { id: string; name: string; lang: string; questionCount: number }

/**
 * The host's question sets as an alternative to categories: choosing one draws the game from it
 * alone, choosing it again goes back to categories. Nothing is offered while the host has no sets.
 */
export function SetPicker({ t, sets, value, onChange }: { t: Translator; sets: readonly HostSet[]; value: string | null; onChange(id: string | null): void }) {
  if (sets.length === 0) return null;
  return (
    <div class="field" style="margin-top:.9rem">
      <label>{t.t("questions.fromSet")}</label>
      <div class="picker" role="group" aria-label={t.t("questions.fromSet")}>
        {sets.map((set) => {
          const on = set.id === value;
          return (
            <button type="button" class={`picker__chip${on ? " is-on" : ""}`} aria-pressed={on} onClick={() => onChange(on ? null : set.id)}>
              {set.name} · {t.num(set.questionCount)}
            </button>
          );
        })}
      </div>
      <p class="faint" style="margin:.35rem 0 0;font-size:var(--step--1)">{value ? t.t("questions.setPicked") : t.t("questions.noSetPicked")}</p>
    </div>
  );
}
