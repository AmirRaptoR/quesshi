import { useEffect, useState } from "preact/hooks";
import { useLocation } from "preact-iso";
import { useApp } from "../../../../client/app";
import { api, ApiError, del, get, post } from "../../../../client/api";
import { Icon } from "../../../../client/components/icon";
import type { PageProps } from "../../../../client/feature";
import { LANGS, type Lang } from "../../../../client/i18n";
import { ImportReport, PromptPanel, SetList, SetQuestions } from "../components/question-sets";
import { canCreateSets, clampCount, copyText, type ImportResult, type QuestionSetView, type SetQuestionView } from "../logic/question-sets";
import { buildPrompt, DIFFICULTIES, SET_COUNTS, type SetDifficulty } from "../logic/set-prompt";

const API = "/api/question-sets";
const Spinner = () => {
  const { t } = useApp();
  return <div class="spinner" role="status" aria-label={t.t("common.loading")} />;
};

function Topbar({ title, back }: { title: string; back: string }) {
  const { t } = useApp();
  return (
    <header class="topbar">
      <a class="icon-hit icon-hit--back" href={back} aria-label={t.t("common.back")}><Icon name="chevron-back" size="1.4rem" /></a>
      <h1 class="topbar__title" dir="auto">{title}</h1>
    </header>
  );
}

/** The player's question sets; a guest is told sets need an account and is offered no way to make one. */
export function SetsPage() {
  const { t, me } = useApp();
  const canCreate = canCreateSets(me);
  const [sets, setSets] = useState<QuestionSetView[] | null>(canCreate ? null : []);
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    if (canCreate) get<QuestionSetView[]>(API).then(setSets, () => setFailed(true));
  }, [canCreate]);
  return (
    <div class="shell">
      <Topbar title={t.t("sets.title")} back="/profile" />
      {failed ? <div class="notice notice--bad">{t.t("common.error")}</div> : sets ? <SetList t={t} sets={sets} canCreate={canCreate} /> : <Spinner />}
    </div>
  );
}

/** Topic, count and difficulty, the prompt they make, and the paste box for the AI's answer. */
function PromptSection({ lang, topic, setTopic, onImport }: { lang: Lang; topic: string; setTopic(topic: string): void; onImport(text: string): Promise<ImportResult | null> }) {
  const { t } = useApp();
  const [count, setCount] = useState<string>(String(SET_COUNTS.initial));
  const [difficulty, setDifficulty] = useState<SetDifficulty>("mixed");
  const [copied, setCopied] = useState(false);
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<ImportResult | null>(null);
  const prompt = buildPrompt({ count: clampCount(count), lang, topic, difficulty });

  const run = async () => {
    setBusy(true);
    setResult(null);
    const done = await onImport(text);
    setBusy(false);
    if (done) {
      setResult(done);
      setText("");
    }
  };
  const readFile = (file: File) => void file.text().then(setText, () => {});

  return (
    <>
      <div class="field" style="margin-top:1rem">
        <label for="set-topic">{t.t("sets.topic")}</label>
        <input id="set-topic" class="input" dir="auto" maxLength={120} value={topic} onInput={(e) => setTopic(e.currentTarget.value)} />
      </div>
      <div class="field" style="margin-top:1rem">
        <label for="set-count">{t.format("sets.count", SET_COUNTS.min, SET_COUNTS.max)}</label>
        <input id="set-count" class="input" type="number" inputMode="numeric" min={SET_COUNTS.min} max={SET_COUNTS.max} value={count}
          onInput={(e) => setCount(e.currentTarget.value)} onBlur={() => setCount(String(clampCount(count)))} />
      </div>
      <div class="field" style="margin:1rem 0">
        <label for="set-difficulty">{t.t("sets.difficulty")}</label>
        <select id="set-difficulty" class="select" value={difficulty} onChange={(e) => setDifficulty(e.currentTarget.value as SetDifficulty)}>
          {DIFFICULTIES.map((level) => <option value={level}>{t.t(`sets.difficulty.${level}`)}</option>)}
        </select>
      </div>
      <PromptPanel t={t} prompt={prompt} copied={copied} onCopy={() => void copyText(prompt).then((ok) => setCopied(ok))}
        text={text} onText={setText} onFile={readFile} busy={busy} onImport={() => void run()} />
      {result && <ImportReport t={t} result={result} />}
    </>
  );
}

/** Turns a refusal into the line to show; the code is the server's. */
const failureText = (failure: unknown, t: ReturnType<typeof useApp>["t"]) =>
  failure instanceof ApiError && t.t(`sets.err.${failure.code}`) !== `sets.err.${failure.code}` ? t.t(`sets.err.${failure.code}`) : t.t("common.error");

/** A new set: its name and language, then the prompt and the paste box; the set is made with its first import. */
export function NewSetPage() {
  const { t, me } = useApp();
  const [name, setName] = useState("");
  const [lang, setLang] = useState<Lang>(me?.lang ?? "en");
  const [topic, setTopic] = useState("");
  const [created, setCreated] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const onImport = async (text: string) => {
    setError(null);
    if (!name.trim()) {
      setError(t.t("sets.err.bad_name"));
      return null;
    }
    try {
      let id = created;
      if (!id) {
        id = (await post<QuestionSetView>(API, { name: name.trim(), lang })).id;
        setCreated(id);
      }
      return await post<ImportResult>(`${API}/${id}/import`, { text });
    } catch (failure) {
      setError(failureText(failure, t));
      return null;
    }
  };

  return (
    <div class="shell">
      <Topbar title={t.t("sets.new")} back="/sets" />
      <section class="card card--taq enter">
        <div class="field">
          <label for="set-name">{t.t("sets.name")}</label>
          <input id="set-name" class="input" dir="auto" maxLength={60} value={name} disabled={created !== null} onInput={(e) => setName(e.currentTarget.value)} />
        </div>
        <div class="field" style="margin-top:1rem">
          <label for="set-lang">{t.t("sets.language")}</label>
          <select id="set-lang" class="select" value={lang} disabled={created !== null} onChange={(e) => setLang(e.currentTarget.value as Lang)}>
            {LANGS.map((code) => <option value={code}>{t.t(`lang.${code}`)}</option>)}
          </select>
        </div>
        <PromptSection lang={lang} topic={topic} setTopic={setTopic} onImport={onImport} />
        {error && <div class="notice notice--bad enter" style="margin-top:.8rem">{error}</div>}
        {created && <a class="btn btn--ghost" style="margin-top:.8rem" href={`/sets/${created}`}>{t.t("sets.open")}</a>}
      </section>
    </div>
  );
}

/** One set: its questions (each can go), rename, delete the set, and import more. */
export function SetPage({ params }: PageProps) {
  const { t } = useApp();
  const { route } = useLocation();
  const id = params.id ?? "";
  const [set, setSet] = useState<QuestionSetView | null>(null);
  const [questions, setQuestions] = useState<SetQuestionView[] | null>(null);
  const [missing, setMissing] = useState(false);
  const [renaming, setRenaming] = useState<string | null>(null);
  const [topic, setTopic] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [confirming, setConfirming] = useState(false);

  const load = async () => {
    try {
      const [sets, list] = await Promise.all([get<QuestionSetView[]>(API), get<SetQuestionView[]>(`${API}/${id}/questions`)]);
      const found = sets.find((s) => s.id === id) ?? null;
      if (!found) return setMissing(true);
      setSet(found);
      setQuestions(list);
      setTopic((current) => current || found.name);
    } catch {
      setMissing(true);
    }
  };
  useEffect(() => { void load(); }, [id]);

  const guard = async (action: () => Promise<unknown>) => {
    setError(null);
    try {
      await action();
    } catch (failure) {
      setError(failureText(failure, t));
    }
  };
  const rename = () => guard(async () => {
    await api("PATCH", `${API}/${id}`, { name: renaming });
    setRenaming(null);
    await load();
  });
  const removeSet = () => guard(async () => {
    await del(`${API}/${id}`);
    route("/sets");
  });
  const removeQuestion = (question: SetQuestionView) => guard(async () => {
    await del(`${API}/${id}/questions/${question.id}`);
    await load();
  });
  const onImport = async (text: string) => {
    setError(null);
    try {
      const result = await post<ImportResult>(`${API}/${id}/import`, { text });
      await load();
      return result;
    } catch (failure) {
      setError(failureText(failure, t));
      return null;
    }
  };

  if (missing) return <div class="shell"><Topbar title={t.t("sets.title")} back="/sets" /><div class="notice notice--bad">{t.t("sets.notFound")}</div></div>;
  if (!set || !questions) return <div class="shell"><Spinner /></div>;
  return (
    <div class="shell">
      <Topbar title={set.name} back="/sets" />
      <p class="muted">{t.format("sets.summary", set.questionCount, t.t(`lang.${set.lang}`))}</p>
      {error && <div class="notice notice--bad enter">{error}</div>}
      {renaming === null ? (
        <div class="row" style="flex-wrap:wrap;gap:.5rem">
          <button type="button" class="btn btn--ghost btn--sm" onClick={() => setRenaming(set.name)}>{t.t("sets.rename")}</button>
          <button type="button" class="btn btn--ghost btn--sm" onClick={() => setConfirming(true)}>{t.t("sets.delete")}</button>
        </div>
      ) : (
        <div class="row" style="flex-wrap:wrap;gap:.5rem">
          <input class="input" style="flex:1 1 10rem;min-width:0" dir="auto" maxLength={60} aria-label={t.t("sets.name")} value={renaming}
            onInput={(e) => setRenaming(e.currentTarget.value)} />
          <button type="button" class="btn btn--sm" disabled={!renaming.trim()} onClick={() => void rename()}>{t.t("profile.save")}</button>
          <button type="button" class="btn btn--ghost btn--sm" onClick={() => setRenaming(null)}>{t.t("admin.cancel")}</button>
        </div>
      )}
      {confirming && (
        <div class="notice notice--bad enter" style="margin-top:.8rem">
          <p style="overflow-wrap:anywhere">{t.t("sets.deleteConfirm")}</p>
          <div class="row" style="gap:.5rem">
            <button type="button" class="btn btn--danger btn--sm" onClick={() => void removeSet()}>{t.t("sets.delete")}</button>
            <button type="button" class="btn btn--ghost btn--sm" onClick={() => setConfirming(false)}>{t.t("admin.cancel")}</button>
          </div>
        </div>
      )}
      <h2 style="margin:1.2rem 0 .6rem">{t.t("sets.questions")}</h2>
      <SetQuestions t={t} questions={questions} onDelete={(question) => void removeQuestion(question)} />
      <h2 style="margin:1.5rem 0 .2rem">{t.t("sets.importMore")}</h2>
      <PromptSection lang={set.lang} topic={topic} setTopic={setTopic} onImport={onImport} />
    </div>
  );
}
