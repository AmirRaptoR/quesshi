import { useApp } from "../../../client/app";
import type { PayloadFormProps } from "../../../client/feature";
import { addChoice, choicesOf, MAX_CHOICES, MIN_CHOICES, removeChoice } from "./choices";

/** Today's fixed-choices editor: two to eight choices, each removable down to two. */
export function ChoiceForm({ payload, onChange }: PayloadFormProps) {
  const { t } = useApp();
  const choices = choicesOf(payload);
  const set = (next: string[]) => onChange({ options: next });
  return (
    <div class="field">
      <label>{t.t("admin.voting.choices")}</label>
      {choices.map((choice, index) => (
        <div class="row" style="gap:.4rem;margin-bottom:.4rem">
          <input class="input grow" value={choice} aria-label={t.format("admin.voting.choiceNumber", index + 1)}
            onInput={(e) => set(choices.map((c, i) => (i === index ? e.currentTarget.value : c)))} />
          <button type="button" class="btn btn--ghost btn--sm btn--icon" disabled={choices.length <= MIN_CHOICES} aria-label={t.t("admin.voting.removeChoice")} title={t.t("admin.voting.removeChoice")}
            onClick={() => set(removeChoice(choices, index))}>−</button>
        </div>
      ))}
      <button type="button" class="btn btn--ghost btn--sm" disabled={choices.length >= MAX_CHOICES} onClick={() => set(addChoice(choices))}>+ {t.t("admin.voting.addChoice")}</button>
    </div>
  );
}

/** A roster question's answers are the players themselves; there is nothing to edit. */
export function RosterForm() {
  return null;
}

export function ChoicePreview({ payload }: { payload: unknown }) {
  const options = ((payload as { options?: unknown } | null)?.options as string[] | undefined) ?? [];
  return (
    <div class="admin-options" dir="auto">
      {options.map((option, index) => <div class="admin-option"><span class="admin-option__key">{index + 1}</span><span>{option}</span></div>)}
    </div>
  );
}

export function RosterPreview() {
  const { t } = useApp();
  return <div class="admin-options"><div class="admin-option"><span class="admin-option__key">↳</span><span>{t.t("admin.voting.participantAnswers")}</span></div></div>;
}
