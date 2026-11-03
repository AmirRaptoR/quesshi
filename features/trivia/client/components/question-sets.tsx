import type { Translator } from "../../../../client/i18n";
import { Icon } from "../../../../client/components/icon";
import { reasonKey, type ImportResult, type QuestionSetView, type SetQuestionView } from "../logic/question-sets";

// These are the screens' bodies without their data loading, so the same markup is rendered by the
// pages and by the layout tests. Long user text wraps (`dir="auto"`, `overflow-wrap`) rather than widening a phone.
const WRAP = "overflow-wrap:anywhere;min-width:0";

/** The player's sets, and the button that starts a new one when they may make one. */
export function SetList({ t, sets, canCreate }: { t: Translator; sets: readonly QuestionSetView[]; canCreate: boolean }) {
  return (
    <>
      {canCreate ? <a class="btn" href="/sets/new">{t.t("sets.create")}</a> : <div class="notice">{t.t("sets.guest")}</div>}
      {sets.length === 0 && canCreate && <p class="muted" style={WRAP}>{t.t("sets.empty")}</p>}
      <ul class="stack" style="list-style:none;padding:0;margin:1rem 0 0">
        {sets.map((set) => (
          <li key={set.id}>
            <a class="card card--flat row row--between" href={`/sets/${set.id}`} style="text-decoration:none;color:inherit;gap:.5rem">
              <strong dir="auto" style={WRAP}>{set.name}</strong>
              <span class="muted" style="white-space:nowrap">{t.format("sets.summary", set.questionCount, t.t(`lang.${set.lang}`))}</span>
            </a>
          </li>
        ))}
      </ul>
    </>
  );
}

/** The prompt to copy, and the paste box (or file) to bring the AI's answer back into. */
export function PromptPanel(props: {
  t: Translator; prompt: string; copied: boolean; onCopy(): void;
  text: string; onText(text: string): void; onFile(file: File): void; busy: boolean; onImport(): void;
}) {
  const { t } = props;
  return (
    <section class="card enter">
      <p style={WRAP}>{t.t("sets.pasteInstruction")}</p>
      <textarea class="textarea" readOnly rows={8} dir="auto" aria-label={t.t("sets.promptLabel")} value={props.prompt} style="font-size:.85rem" />
      <div class="row" style="margin-top:.6rem">
        <button type="button" class="btn btn--sm" onClick={props.onCopy}>{t.t("sets.copy")}</button>
        {props.copied && <span class="notice notice--good" role="status">{t.t("sets.copied")}</span>}
      </div>
      <div class="field" style="margin-top:1rem">
        <label for="set-answer">{t.t("sets.answerLabel")}</label>
        <textarea id="set-answer" class="textarea" rows={6} dir="auto" value={props.text} onInput={(e) => props.onText(e.currentTarget.value)} />
      </div>
      <div class="field" style="margin-top:.6rem">
        <label for="set-file">{t.t("sets.fileLabel")}</label>
        <input id="set-file" class="input" type="file" accept=".json,.csv,application/json,text/csv" style="max-width:100%"
          onChange={(e) => { const file = e.currentTarget.files?.[0]; if (file) props.onFile(file); }} />
      </div>
      <button type="button" class="btn" style="margin-top:.8rem" disabled={props.busy || !props.text.trim()} onClick={props.onImport}>{t.t("sets.import")}</button>
    </section>
  );
}

/** What an import did: how many questions went in, and each row that did not with the reason. */
export function ImportReport({ t, result }: { t: Translator; result: ImportResult }) {
  const has = (key: string) => t.t(key) !== key;
  return (
    <section class="card card--flat enter" role="status">
      <p class="notice notice--good" style={WRAP}>{t.format("sets.accepted", result.accepted)}</p>
      {result.rejected.length > 0 && (
        <>
          <p style={WRAP}>{t.format("sets.rejected", result.rejected.length)}</p>
          <ul>
            {result.rejected.map((row) => (
              <li key={row.row} style={WRAP}>{t.format("sets.rejectedRow", row.row, t.t(reasonKey(row.reason, has)))}</li>
            ))}
          </ul>
        </>
      )}
    </section>
  );
}

/** A set's questions, each with its correct answer and a way to delete it. */
export function SetQuestions({ t, questions, onDelete }: { t: Translator; questions: readonly SetQuestionView[]; onDelete(question: SetQuestionView): void }) {
  if (questions.length === 0) return <p class="muted">{t.t("sets.noQuestions")}</p>;
  return (
    <ul class="stack" style="list-style:none;padding:0;margin:0">
      {questions.map((question) => (
        <li key={question.id} class="card card--flat row row--between" style="align-items:flex-start;gap:.5rem">
          <div style={WRAP}>
            <strong dir="auto">{question.prompt}</strong>
            <div class="muted" dir="auto">{question.payload.options[question.payload.correctIndex]}</div>
          </div>
          <button type="button" class="icon-hit" title={t.t("sets.deleteQuestion")} aria-label={t.t("sets.deleteQuestion")} onClick={() => onDelete(question)}>
            <Icon name="trash" />
          </button>
        </li>
      ))}
    </ul>
  );
}
