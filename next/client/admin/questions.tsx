import { useEffect, useState } from "preact/hooks";
import { useLocation } from "preact-iso";
import { ApiError, del, get, post } from "../api";
import { useApp } from "../app";
import { Icon } from "../components/icon";
import { MediaBlock, type Media } from "../components/media-block";
import { AdminGate, AdminModal, AdminShell, AdminToolbar, download, Pager, sendFile, Spinner } from "./components";
import { clampPage, pageAfterResize, questionFilterSearch, questionsApiQuery, readQuestionFilters, translateOr, type QuestionFilters } from "./logic";
import { type AdminQuestion, type Category, categoryName, KindPreview, kindForm, LANG_CODES, LEVELS, type QuestionKind, useCategories } from "./questions-shared";

interface Draft {
  id: string | null; kind: string; lang: string; categoryId: string; level: number; status: string; prompt: string;
  /** Each kind's payload, so switching kinds and back keeps what was written. */
  payloads: Record<string, unknown>;
  explanation: string; media: Media | null;
}

interface ImportReport { dryRun: boolean; total: number; accepted: number; rejected: number; rows: { row: number; prompt: string | null; accepted: boolean; error: string | null }[] }

const STATUSES = ["pending", "approved", "rejected"];

/** Today's trivia and voting question pages in one: poll questions are kinds of the same bank now. */
export function AdminQuestions() {
  const { t } = useApp();
  return (
    <AdminShell title={t.t("admin.questions")}>
      <AdminGate>{() => <QuestionsBody />}</AdminGate>
    </AdminShell>
  );
}

function QuestionsBody() {
  const { t } = useApp();
  const { query, path, route } = useLocation();
  const categories = useCategories();
  const [kinds, setKinds] = useState<QuestionKind[]>([]);
  const [filters, setFilters] = useState<QuestionFilters>(() => readQuestionFilters(query));
  const [text, setText] = useState(filters.text);
  const [results, setResults] = useState<{ total: number; items: AdminQuestion[] } | null | undefined>(undefined);
  const [editing, setEditing] = useState<Draft | null>(null);
  const [importing, setImporting] = useState(false);

  useEffect(() => {
    get<QuestionKind[]>("/api/admin/question-kinds").then(setKinds).catch(() => {});
  }, []);

  const fetchPage = async (at: QuestionFilters) => {
    const search = questionFilterSearch(at);
    // Replaces rather than pushes: changing a filter should not fill the back button.
    route(search ? `${path}?${search}` : path, true);
    try {
      const result = await get<{ total: number; items: AdminQuestion[] }>(`/api/admin/questions?${questionsApiQuery(at)}`);
      // Approving or deleting the last row of the last page would otherwise leave an empty page.
      const page = clampPage(at.page, result.total, at.size);
      if (page !== at.page) return setFilters({ ...at, page });
      setResults(result);
    } catch (error) {
      if (!(error instanceof ApiError)) throw error;
      setResults(null);
    }
  };
  useEffect(() => {
    void fetchPage(filters);
  }, [filters]);

  /** Changing a filter is a new search, so it starts again at page one. */
  const filter = (change: Partial<QuestionFilters>) => setFilters({ ...filters, ...change, page: 1 });
  const refresh = () => fetchPage(filters);
  const act = async (request: Promise<unknown>) => {
    await request.catch(() => {});
    await refresh();
  };
  const levelled = (kind: string) => kinds.find((k) => k.kind === kind)?.levelled ?? false;

  const startNew = () => setEditing({
    id: null, kind: filters.kind || kinds[0]?.kind || "", lang: "fa", categoryId: categories[0]?.id ?? "", level: 1, status: "pending",
    prompt: "", payloads: {}, explanation: "", media: null,
  });
  const edit = (q: AdminQuestion) => setEditing({
    id: q.id, kind: q.kind, lang: q.lang, categoryId: q.categoryId ?? "", level: q.level ?? 1, status: q.status, prompt: q.prompt,
    payloads: { [q.kind]: q.payload }, explanation: q.explanation ?? "", media: q.media ?? null,
  });

  const select = (label: string, value: string, options: [string, string][], onChange: (value: string) => void) => (
    <select class="select" style="width:auto" value={value} onChange={(e) => onChange(e.currentTarget.value)}>
      <option value="">{t.t(label)}: {t.t("admin.all")}</option>
      {options.map(([v, text]) => <option value={v}>{text}</option>)}
    </select>
  );

  return (
    <>
      {importing && <ImportDialog kinds={kinds} onClose={() => setImporting(false)} onCommitted={() => void refresh()} />}

      <AdminToolbar label={t.t("admin.filters")}
        filters={
          <>
            {select("admin.language", filters.lang, LANG_CODES.map((l) => [l, t.t(`lang.${l}`)]), (lang) => filter({ lang }))}
            {select("admin.category", filters.category, categories.map((c) => [c.id, t.name(c)]), (category) => filter({ category }))}
            {select("admin.status", filters.status, STATUSES.map((s) => [s, t.t(`admin.${s}`)]), (status) => filter({ status }))}
            {select("admin.kind", filters.kind, kinds.map((k) => [k.kind, t.t(`admin.kind.${k.kind}`)]), (kind) => filter({ kind }))}
            <select class="select" style="width:auto" value={String(filters.level)} onChange={(e) => filter({ level: Number(e.currentTarget.value) })}>
              <option value="0">{t.t("admin.level")}: {t.t("admin.all")}</option>
              {LEVELS.map((level) => <option value={String(level)}>{t.t(`level.${level}`)}</option>)}
            </select>
            <input class="input grow" style="min-width:10rem" placeholder={t.t("admin.search")} value={text}
              onInput={(e) => setText(e.currentTarget.value)} onKeyDown={(e) => { if (e.key === "Enter") filter({ text }); }} />
          </>
        }
        actions={
          <>
            <button type="button" class="btn btn--ghost btn--sm" onClick={() => setImporting(true)}>{t.t("admin.import")}</button>
            <button type="button" class="btn btn--sm" disabled={kinds.length === 0} onClick={startNew}>{t.t("admin.new")}</button>
          </>
        } />

      {editing && (
        <QuestionEditor draft={editing} kinds={kinds} categories={categories} levelled={levelled(editing.kind)} onChange={setEditing}
          onClose={() => setEditing(null)} onSaved={() => { setEditing(null); void refresh(); }} />
      )}

      {results === undefined ? <Spinner /> : (
        <>
          {results?.items.length === 0 && <p class="empty">{t.t("admin.noResults")}</p>}
          <div class="stack" style="margin-top:.75rem">
            {(results?.items ?? []).map((question) => (
              <QuestionCard key={question.id} question={question} levelled={levelled(question.kind)} categoryName={categoryName(t, categories, question.categoryId)}
                onApprove={() => void act(post(`/api/admin/questions/${question.id}/approve`))}
                onReject={() => void act(post(`/api/admin/questions/${question.id}/reject`))}
                onEdit={() => edit(question)}
                onDelete={() => void act(del(`/api/admin/questions/${question.id}`))} />
            ))}
          </div>
          <Pager total={results?.total ?? 0} page={filters.page} size={filters.size}
            onPage={(page) => setFilters({ ...filters, page })}
            onSize={(size) => setFilters({ ...filters, size, page: pageAfterResize(filters.page, filters.size, size) })} />
        </>
      )}
    </>
  );
}

function QuestionCard({ question, levelled, categoryName, onApprove, onReject, onEdit, onDelete }: {
  question: AdminQuestion; levelled: boolean; categoryName: string; onApprove(): void; onReject(): void; onEdit(): void; onDelete(): void;
}) {
  const { t } = useApp();
  const statusClass = question.status === "approved" ? "is-active" : question.status === "rejected" ? "is-inactive" : "";
  return (
    <article class="admin-question">
      <header class="admin-question__head">
        <div class="admin-question__meta">
          <span class="chip" dir="ltr">{question.lang}</span>
          <span class="chip">{categoryName}</span>
          {levelled && question.level !== null && <span class="chip">{t.t(`level.${question.level}`)}</span>}
          <span class={`admin-status ${statusClass}`}>{t.t(`admin.${question.status}`)}</span>
          <span class="chip chip--kind">{t.t(`admin.kind.${question.kind}`)}</span>
        </div>
        <div class="admin-question__actions">
          {question.status !== "approved" && <button type="button" class="btn btn--sm" onClick={onApprove}>{t.t("admin.approve")}</button>}
          {question.status !== "rejected" && <button type="button" class="btn btn--ghost btn--sm" onClick={onReject}>{t.t("admin.reject")}</button>}
          <button type="button" class="btn btn--ghost btn--sm" onClick={onEdit}>{t.t("admin.edit")}</button>
          <button type="button" class="icon-hit admin-danger" title={t.t("admin.delete")} aria-label={t.t("admin.delete")} onClick={onDelete}><Icon name="trash" /></button>
        </div>
      </header>
      <div class="admin-question__body">
        <p class="admin-question__prompt" dir="auto">{question.prompt}</p>
        <MediaBlock media={question.media ?? null} compact autoplay={false} loop={false} />
        <KindPreview kind={question.kind} payload={question.payload} />
        {question.explanation && <p class="muted" dir="auto" style="margin:.7rem 0 0">{question.explanation}</p>}
      </div>
    </article>
  );
}

function QuestionEditor({ draft, kinds, categories, levelled, onChange, onClose, onSaved }: {
  draft: Draft; kinds: QuestionKind[]; categories: Category[]; levelled: boolean; onChange(draft: Draft): void; onClose(): void; onSaved(): void;
}) {
  const { t } = useApp();
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const PayloadForm = kindForm(draft.kind);
  const set = (change: Partial<Draft>) => onChange({ ...draft, ...change });

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      await post("/api/admin/questions", {
        ...(draft.id ? { id: draft.id } : {}), kind: draft.kind, lang: draft.lang, categoryId: draft.categoryId || null,
        level: levelled ? draft.level : null, status: draft.status, prompt: draft.prompt, payload: draft.payloads[draft.kind],
        explanation: draft.explanation, media: draft.media,
      });
      onSaved();
    } catch (failure) {
      if (!(failure instanceof ApiError)) throw failure;
      // The server names the broken rule; an admin should never read a raw code out of a form.
      setError(translateOr(t, `admin.err.${failure.code}`, "common.error"));
    } finally {
      setBusy(false);
    }
  };

  const upload = async (file: File | undefined) => {
    if (!file) return;
    try {
      const media = await sendFile<{ kind: Media["kind"]; url: string }>(`/api/admin/media?name=${encodeURIComponent(file.name)}`, file);
      set({ media: { kind: media.kind, url: media.url, attribution: null } });
    } catch (failure) {
      if (!(failure instanceof ApiError)) throw failure;
      setError(t.t("common.error"));
    }
  };

  return (
    <AdminModal title={draft.id ? t.t("admin.edit") : t.t("admin.new")} onClose={onClose}>
      <div class="stack" style="margin-top:.9rem">
        <div class="row" style="flex-wrap:wrap;gap:.5rem">
          <select class="select" style="width:auto" value={draft.lang} onChange={(e) => set({ lang: e.currentTarget.value })}>
            {LANG_CODES.map((l) => <option value={l}>{t.t(`lang.${l}`)}</option>)}
          </select>
          <select class="select" style="width:auto" value={draft.categoryId} onChange={(e) => set({ categoryId: e.currentTarget.value })}>
            <option value="">{t.t("admin.uncategorized")}</option>
            {categories.map((c) => <option value={c.id}>{t.name(c)}</option>)}
          </select>
          {levelled && (
            <select class="select" style="width:auto" value={String(draft.level)} onChange={(e) => set({ level: Number(e.currentTarget.value) })}>
              {LEVELS.map((level) => <option value={String(level)}>{t.t(`level.${level}`)}</option>)}
            </select>
          )}
          <select class="select" style="width:auto" value={draft.status} onChange={(e) => set({ status: e.currentTarget.value })}>
            {STATUSES.map((s) => <option value={s}>{t.t(`admin.${s}`)}</option>)}
          </select>
        </div>

        {/* The kind decides what the rest of this form means, so its options are all in view. */}
        <div class="field">
          <label id="kind-label">{t.t("admin.kind")}</label>
          <div class="segmented" role="radiogroup" aria-labelledby="kind-label">
            {kinds.map(({ kind }) => (
              <button type="button" class={`segmented__option${draft.kind === kind ? " is-on" : ""}`} role="radio" aria-checked={draft.kind === kind}
                onClick={() => set({ kind })}>{t.t(`admin.kind.${kind}`)}</button>
            ))}
          </div>
        </div>

        <div class="field">
          <label for="question-prompt">{t.t("admin.prompt")}</label>
          <textarea id="question-prompt" class="textarea" rows={2} value={draft.prompt} onChange={(e) => set({ prompt: e.currentTarget.value })} />
        </div>

        {PayloadForm && <PayloadForm payload={draft.payloads[draft.kind]} onChange={(payload) => set({ payloads: { ...draft.payloads, [draft.kind]: payload } })} />}

        <div class="field">
          <label for="question-explanation">{t.t("admin.explanation")}</label>
          <input id="question-explanation" class="input" value={draft.explanation} onChange={(e) => set({ explanation: e.currentTarget.value })} />
        </div>

        <div class="field">
          <label for="question-media">{t.t("admin.media")}</label>
          <div class="row">
            <input id="question-media" type="file" class="input grow" accept="image/*,audio/*,video/*" onChange={(e) => void upload(e.currentTarget.files?.[0])} />
            {draft.media && (
              <>
                <span class="chip">{draft.media.kind}</span>
                {/* Detaches the media; the uploaded file stays on disk. */}
                <button class="btn btn--ghost btn--sm btn--icon" title={t.t("admin.removeMedia")} aria-label={t.t("admin.removeMedia")} onClick={() => set({ media: null })}>
                  <Icon name="close" />
                </button>
              </>
            )}
          </div>
          {draft.media && <MediaBlock media={draft.media} compact autoplay={false} loop={false} />}
        </div>

        {error && <div class="notice notice--bad">{error}</div>}
        <div class="row">
          <button class="btn" disabled={busy} onClick={() => void save()}>{t.t("admin.save")}</button>
          <button class="btn btn--ghost" onClick={onClose}>{t.t("admin.cancel")}</button>
        </div>
      </div>
    </AdminModal>
  );
}

function ImportDialog({ kinds, onClose, onCommitted }: { kinds: QuestionKind[]; onClose(): void; onCommitted(): void }) {
  const { t } = useApp();
  const [chosen, setKind] = useState("");
  // The kinds may arrive after the dialog opens; until one is chosen, the first is meant.
  const kind = chosen || kinds[0]?.kind || "";
  const [format, setFormat] = useState<"csv" | "json">("csv");
  const [file, setFile] = useState<File | null>(null);
  const [report, setReport] = useState<ImportReport | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const run = async (dryRun: boolean) => {
    if (!file) return;
    setBusy(true);
    setError(null);
    try {
      const result = await sendFile<ImportReport>(`/api/admin/questions/import?kind=${encodeURIComponent(kind)}&format=${format}&dryRun=${dryRun}`, file);
      setReport(result);
      if (!dryRun) onCommitted();
    } catch (failure) {
      if (!(failure instanceof ApiError)) throw failure;
      setReport(null);
      setError(translateOr(t, `admin.err.${failure.code}`, "common.error"));
    } finally {
      setBusy(false);
    }
  };
  const template = () => download(`/api/admin/questions/import/template?kind=${encodeURIComponent(kind)}&format=${format}`, `${kind}-template.${format}`).catch(() => setError(t.t("common.error")));

  return (
    <AdminModal title={t.t("admin.import")} onClose={onClose}>
      <div class="row" style="flex-wrap:wrap;gap:.5rem;margin-top:.6rem">
        <select class="select" style="width:auto" value={kind} onChange={(e) => { setKind(e.currentTarget.value); setReport(null); }}>
          {kinds.map((k) => <option value={k.kind}>{t.t(`admin.kind.${k.kind}`)}</option>)}
        </select>
        <select class="select" style="width:auto" value={format} onChange={(e) => { setFormat(e.currentTarget.value === "json" ? "json" : "csv"); setReport(null); }}>
          <option value="csv">CSV</option>
          <option value="json">JSON</option>
        </select>
        <button class="btn btn--ghost btn--sm" disabled={!kind} onClick={() => void template()}>{t.t("admin.import.template")}</button>
        <input type="file" onChange={(e) => { setFile(e.currentTarget.files?.[0] ?? null); setReport(null); }} />
        <button class="btn btn--sm" disabled={!file || !kind || busy} onClick={() => void run(true)}>{busy ? t.t("admin.import.working") : t.t("admin.import.dryRun")}</button>
        {report?.dryRun && report.accepted > 0 && (
          <button class="btn btn--gold btn--sm" disabled={busy} onClick={() => void run(false)}>{t.t("admin.import.commit")}</button>
        )}
      </div>
      <p class="faint" style="margin:.5rem 0 0;font-size:var(--step--1)">{t.t("admin.import.hint")}</p>
      {error && <div class="notice notice--bad" style="margin-top:.7rem">{error}</div>}
      {report && (
        <>
          <div class={`notice${!report.dryRun && report.rejected === 0 ? " notice--good" : ""}`} style="margin-top:.7rem">
            {t.format("admin.import.summary", report.total, report.accepted, report.rejected)}
            {report.dryRun ? "" : ` · ${t.t("admin.import.committed")}`}
          </div>
          {report.rows.some((r) => !r.accepted) && (
            <table class="table" style="margin-top:.5rem">
              <thead><tr><th>#</th><th>{t.t("admin.prompt")}</th><th>{t.t("admin.import.error")}</th></tr></thead>
              <tbody>
                {report.rows.filter((r) => !r.accepted).map((r) => <tr key={r.row}><td>{r.row}</td><td>{r.prompt ?? "—"}</td><td dir="ltr">{r.error}</td></tr>)}
              </tbody>
            </table>
          )}
        </>
      )}
    </AdminModal>
  );
}
