import { useEffect, useState } from "preact/hooks";
import { useLocation } from "preact-iso";
import { ApiError, del, get, post } from "../api";
import { useApp } from "../app";
import { Icon } from "../components/icon";
import { MediaBlock } from "../components/media-block";
import { clientFeatures } from "../features";
import { AdminGate, AdminTopbar, Pager, Spinner } from "./components";
import { clampPage, problemsText, readPaging, reportLabel, reportReasonKey, stamp, tint } from "./logic";
import { type AdminQuestion, categoryName, KindPreview, useCategories } from "./questions-shared";
import type { AdminMe } from "./session";

/** Loads a page's data once the admin is confirmed; null after a failure. */
function useLoad<T>(path: string | null): { data: T | null | undefined; reload(): Promise<void> } {
  const [data, setData] = useState<T | null | undefined>(undefined);
  const reload = async () => {
    if (path === null) return;
    try {
      setData(await get<T>(path));
    } catch (error) {
      if (!(error instanceof ApiError)) throw error;
      setData(null);
    }
  };
  useEffect(() => {
    void reload();
  }, [path]);
  return { data, reload };
}

interface Dashboard { players: number; questions: { total: number; approved: number; pending: number; rejected: number }; sessionsPlayed: number; openRooms: number }

export function AdminDashboard() {
  const { t } = useApp();
  return (
    <AdminTopbar title={t.t("nav.admin")}>
      <AdminGate>{() => <DashboardBody />}</AdminGate>
    </AdminTopbar>
  );
}

function DashboardBody() {
  const { t } = useApp();
  const { data } = useLoad<Dashboard>("/api/admin/dashboard");
  if (data === undefined) return <Spinner />;
  if (data === null) return <p class="empty">{t.t("common.error")}</p>;
  const stat = (value: number, label: string) => <div class="card stat"><div class="stat__value">{t.num(value)}</div><div class="stat__label">{t.t(label)}</div></div>;
  return (
    <div class="grid-2 enter">
      {stat(data.players, "admin.players")}
      {stat(data.questions.approved, "admin.approved")}
      {stat(data.questions.pending, "admin.pending")}
      {stat(data.sessionsPlayed, "admin.matches")}
      {stat(data.openRooms, "admin.live")}
    </div>
  );
}

interface Account { id: string; username: string; email: string; active: boolean; mustChangePassword: boolean; lastLoginAt: string | null; locked: boolean }

export function AdminAccounts() {
  const { t } = useApp();
  return (
    <AdminTopbar title={t.t("admin.accounts")}>
      <AdminGate>{(admin) => <AccountsBody me={admin} />}</AdminGate>
    </AdminTopbar>
  );
}

function AccountsBody({ me }: { me: AdminMe }) {
  const { t } = useApp();
  const { data, reload } = useLoad<Account[]>("/api/admin/accounts");
  const [form, setForm] = useState({ username: "", email: "", password: "" });
  const [message, setMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  if (data === undefined) return <Spinner />;

  const create = async () => {
    setBusy(true);
    setMessage(null);
    try {
      await post("/api/admin/accounts", { username: form.username.trim(), email: form.email.trim(), password: form.password });
      setForm({ username: "", email: "", password: "" });
      await reload();
    } catch (failure) {
      if (!(failure instanceof ApiError)) throw failure;
      setMessage(problemsText(t, failure));
    } finally {
      setBusy(false);
    }
  };
  const setActive = async (account: Account, value: boolean) => {
    await post(`/api/admin/accounts/${account.id}/active`, { value }).catch(() => {});
    await reload();
  };
  const remove = async (account: Account) => {
    try {
      await del(`/api/admin/accounts/${account.id}`);
    } catch (failure) {
      if (!(failure instanceof ApiError)) throw failure;
      setMessage(t.t("admin.auth.lastAdmin"));
    }
    await reload();
  };

  return (
    <>
      <div class="scroll-x card card--flat">
        <table class="table">
          <thead>
            <tr><th>{t.t("admin.auth.username")}</th><th>{t.t("auth.email")}</th><th>{t.t("admin.status")}</th><th>{t.t("admin.accounts.lastLogin")}</th><th /></tr>
          </thead>
          <tbody>
            {(data ?? []).map((account) => (
              <tr key={account.id}>
                <td>
                  <div class="row" style="gap:.4rem">
                    <strong dir="ltr">{account.username}</strong>
                    {account.id === me.id && <span class="chip chip--live">{t.t("result.you")}</span>}
                    {account.mustChangePassword && <span class="chip">{t.t("admin.accounts.temporary")}</span>}
                  </div>
                </td>
                <td dir="ltr" class="faint">{account.email}</td>
                <td>
                  {account.locked ? <span class="chip chip--loss">{t.t("admin.accounts.locked")}</span>
                    : account.active ? <span class="chip chip--win">{t.t("admin.accounts.active")}</span>
                    : <span class="chip">{t.t("admin.accounts.disabled")}</span>}
                </td>
                <td dir="ltr" class="faint">{account.lastLoginAt ? stamp(account.lastLoginAt) : t.t("admin.accounts.never")}</td>
                <td>
                  <div class="row" style="gap:.3rem;justify-content:flex-end">
                    {account.id !== me.id && (
                      <>
                        <button class="btn btn--ghost btn--sm" onClick={() => void setActive(account, !account.active)}>
                          {account.active ? t.t("admin.accounts.disable") : t.t("admin.accounts.enable")}
                        </button>
                        <button class="btn btn--sm btn--icon btn--icon-danger" title={t.t("admin.delete")} aria-label={t.t("admin.delete")} onClick={() => void remove(account)}>
                          <Icon name="trash" />
                        </button>
                      </>
                    )}
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <section class="card card--taq" style="margin-top:1.25rem">
        <span class="eyebrow">{t.t("admin.accounts.new")}</span>
        <div class="stack" style="margin-top:.9rem">
          <div class="grid-2">
            <div class="field">
              <label for="new-username">{t.t("admin.auth.username")}</label>
              <input id="new-username" class="input" dir="ltr" value={form.username} onChange={(e) => setForm({ ...form, username: e.currentTarget.value })} />
            </div>
            <div class="field">
              <label for="new-email">{t.t("auth.email")}</label>
              <input id="new-email" class="input" dir="ltr" type="email" required value={form.email} onChange={(e) => setForm({ ...form, email: e.currentTarget.value })} />
              <span class="faint" style="font-size:var(--step--1)">{t.t("admin.auth.badEmail")}</span>
            </div>
          </div>
          <div class="field">
            <label for="new-password">{t.t("admin.auth.password")}</label>
            <input id="new-password" class="input" dir="ltr" type="password" autoComplete="new-password" value={form.password} onChange={(e) => setForm({ ...form, password: e.currentTarget.value })} />
            <span class="faint" style="font-size:var(--step--1)">{t.t("password.rules")} {t.t("admin.accounts.temporary")}.</span>
          </div>
          {message && <div class="notice notice--bad">{message}</div>}
          <div class="row"><button class="btn" disabled={busy} onClick={() => void create()}>{t.t("admin.accounts.new")}</button></div>
        </div>
      </section>
    </>
  );
}

interface Member { id: string; displayName: string; email: string | null; avatarSeed: string; banned: boolean }

export function AdminUsers() {
  const { t } = useApp();
  return (
    <AdminTopbar title={t.t("admin.users")}>
      <AdminGate>{() => <UsersBody />}</AdminGate>
    </AdminTopbar>
  );
}

function UsersBody() {
  const { t } = useApp();
  const { query, path, route } = useLocation();
  const [text, setText] = useState(query.q ?? "");
  const [searched, setSearched] = useState(query.q ?? "");
  const { data, reload } = useLoad<{ total: number; items: Member[] }>(`/api/admin/users?q=${encodeURIComponent(searched.trim())}`);
  const search = () => {
    const q = text.trim();
    // Replaces rather than pushes: a search should not fill the back button.
    route(q ? `${path}?q=${encodeURIComponent(q)}` : path, true);
    if (q === searched.trim()) void reload();
    setSearched(q);
  };
  const setBanned = async (member: Member, value: boolean) => {
    await post(`/api/admin/users/${member.id}/ban`, { value }).catch(() => {});
    await reload();
  };
  return (
    <>
      <div class="row" style="margin-bottom:1rem">
        <input class="input grow" placeholder={t.t("admin.search")} value={text} onInput={(e) => setText(e.currentTarget.value)} onKeyDown={(e) => { if (e.key === "Enter") search(); }} />
        <button class="btn btn--sm" onClick={search}>{t.t("admin.search")}</button>
      </div>
      {data === undefined ? <Spinner /> : (
        <div class="scroll-x card card--flat">
          <table class="table">
            <thead><tr><th>{t.t("admin.name")}</th><th>{t.t("auth.email")}</th><th /></tr></thead>
            <tbody>
              {(data?.items ?? []).map((member) => (
                <tr key={member.id}>
                  <td>
                    <div class="row" style="gap:.5rem">
                      <span class="avatar" style={`width:1.75rem;height:1.75rem;font-size:.8rem;background:${tint(member.avatarSeed)}`}>
                        {member.displayName ? member.displayName.slice(0, 1).toUpperCase() : "?"}
                      </span>
                      <span>{member.displayName}</span>
                      {member.banned && <span class="chip chip--loss">banned</span>}
                    </div>
                  </td>
                  <td dir="ltr" class="faint">{member.email}</td>
                  <td>
                    <div class="row" style="gap:.3rem;justify-content:flex-end">
                      <button class={`btn ${member.banned ? "btn--ghost" : "btn--danger"} btn--sm`} onClick={() => void setBanned(member, !member.banned)}>
                        {member.banned ? t.t("admin.unban") : t.t("admin.ban")}
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}

interface ReportedQuestion extends AdminQuestion { reports: { playerId: string; displayName: string; reason: string; at: string }[] }

export function AdminReported() {
  const { t } = useApp();
  return (
    <AdminTopbar title={t.t("admin.reported")}>
      <AdminGate>{() => <ReportedBody />}</AdminGate>
    </AdminTopbar>
  );
}

function ReportedBody() {
  const { t } = useApp();
  const { query, path, route } = useLocation();
  const categories = useCategories();
  const [paging, setPaging] = useState(() => readPaging(query));
  const [page, setPage] = useState<{ total: number; items: ReportedQuestion[] } | null | undefined>(undefined);

  const fetchPage = async (at: { page: number; size: number }) => {
    const search = new URLSearchParams();
    if (at.page !== 1) search.set("page", String(at.page));
    if (at.size !== 25) search.set("size", String(at.size));
    route(search.size ? `${path}?${search}` : path, true);
    try {
      const result = await get<{ total: number; items: ReportedQuestion[] }>(`/api/admin/reported?skip=${(at.page - 1) * at.size}&take=${at.size}`);
      // Clearing the last item on the last page would otherwise leave an empty screen.
      const clamped = clampPage(at.page, result.total, at.size);
      if (clamped !== at.page) return setPaging({ ...at, page: clamped });
      setPage(result);
    } catch (error) {
      if (!(error instanceof ApiError)) throw error;
      setPage(null);
    }
  };
  useEffect(() => {
    void fetchPage(paging);
  }, [paging.page, paging.size]);

  const act = async (request: Promise<unknown>) => {
    await request.catch(() => {});
    await fetchPage(paging);
  };
  if (page === undefined) return <Spinner />;
  if (!page || page.items.length === 0) return <p class="empty">{t.t("admin.noReports")}</p>;
  const pager = <Pager total={page.total} page={paging.page} size={paging.size} onPage={(n) => setPaging({ ...paging, page: n })} onSize={(size) => setPaging({ page: 1, size })} />;
  return (
    <>
      {pager}
      <div class="stack" style="margin-top:.75rem">
        {page.items.map((question) => (
          <article class="card" key={question.id}>
            <div class="row row--between" style="gap:.5rem;flex-wrap:wrap">
              <div class="row" style="gap:.4rem;flex-wrap:wrap">
                <span class="chip chip--loss">{reportLabel(t, question.reports.length)}</span>
                <span class="chip" dir="ltr">{question.lang}</span>
                <span class="chip">{categoryName(t, categories, question.categoryId)}</span>
                {question.level !== null && <span class="chip">{t.t(`level.${question.level}`)}</span>}
                {question.status === "pending" && <span class="chip chip--loss">{t.t("admin.suppressed")}</span>}
              </div>
              <div class="row" style="gap:.35rem">
                <button class="btn btn--sm" onClick={() => void act(post(`/api/admin/questions/${question.id}/dismiss-reports`))}>{t.t("admin.dismiss")}</button>
                <button class="btn btn--ghost btn--sm" onClick={() => void act(post(`/api/admin/questions/${question.id}/reject`))}>{t.t("admin.reject")}</button>
                <button class="btn btn--sm btn--icon btn--icon-danger" title={t.t("admin.delete")} aria-label={t.t("admin.delete")} onClick={() => void act(del(`/api/admin/questions/${question.id}`))}>
                  <Icon name="trash" />
                </button>
              </div>
            </div>
            <p dir="auto" style="margin:.6rem 0 .4rem;font-weight:600">{question.prompt}</p>
            <MediaBlock media={question.media ?? null} compact autoplay={false} loop={false} />
            <KindPreview kind={question.kind} payload={question.payload} />
            {question.explanation && <p class="muted" dir="auto" style="margin:.55rem 0 0;font-size:var(--step--1)">{question.explanation}</p>}
            <div style="margin-top:.7rem;border-top:1px solid var(--line);padding-top:.55rem">
              <span class="eyebrow">{t.t("admin.reportedBy")}</span>
              <div class="stack" style="gap:.25rem;margin-top:.4rem">
                {question.reports.map((report) => (
                  <div class="row" style="gap:.5rem;font-size:var(--step--1)" key={`${report.playerId}-${report.at}`}>
                    <span class="chip chip--loss">{t.t(reportReasonKey(report.reason))}</span>
                    <span class="grow muted">{report.displayName}</span>
                    <span class="faint" dir="ltr">{stamp(report.at)}</span>
                  </div>
                ))}
              </div>
            </div>
          </article>
        ))}
      </div>
      {pager}
    </>
  );
}

interface Room {
  id: string; format: string; code: string; owner: string; phase: string; createdAt: number;
  settings: { lang?: unknown }; slot?: number; total?: number;
  /** Only lobbies carry their roster; a running game's would be most of every broadcast. */
  participants?: { id: string; displayName: string }[];
}

export function AdminLive() {
  const { t } = useApp();
  return (
    <AdminTopbar title={t.t("admin.live")}>
      <AdminGate>{() => <LiveBody />}</AdminGate>
    </AdminTopbar>
  );
}

function LiveBody() {
  const { t } = useApp();
  const { data, reload } = useLoad<Room[]>("/api/admin/rooms");
  const [confirming, setConfirming] = useState<string | null>(null);
  const panels = clientFeatures.flatMap((feature) => feature.adminLivePanels ?? []);
  const end = async (id: string) => {
    setConfirming(null);
    await post(`/api/admin/rooms/${id}/end`).catch(() => {});
    await reload();
  };
  return (
    <>
      <div class="row row--between" style="margin-bottom:1rem">
        <span class="faint">{t.format("admin.live.total", data?.length ?? 0)}</span>
        <button class="btn btn--sm" onClick={() => void reload()}>{t.t("admin.live.refresh")}</button>
      </div>
      {panels.map((Panel, i) => <Panel key={i} />)}
      {data === undefined ? <Spinner /> : data === null ? <p class="empty">{t.t("common.error")}</p> : data.length === 0 ? <p class="empty">{t.t("admin.live.none")}</p> : (
        <div class="scroll-x card card--flat">
          <table class="table">
            <thead>
              <tr>
                <th>{t.t("admin.live.code")}</th><th>{t.t("admin.live.challenger")}</th><th>{t.t("admin.live.opponent")}</th><th>{t.t("admin.language")}</th>
                <th>{t.t("admin.live.round")}</th><th>{t.t("admin.live.phase")}</th><th>{t.t("admin.live.startedAt")}</th><th />
              </tr>
            </thead>
            <tbody>
              {data.map((room) => {
                const owner = room.participants?.find((p) => p.id === room.owner);
                const others = room.participants?.filter((p) => p.id !== room.owner).map((p) => p.displayName).join("، ");
                return (
                  <tr key={room.id}>
                    <td dir="ltr">{room.code}</td>
                    <td>{owner?.displayName ?? "—"}</td>
                    <td>{others || t.t("admin.live.noOpponent")}</td>
                    <td dir="ltr">{typeof room.settings.lang === "string" ? room.settings.lang : ""}</td>
                    <td class="numeral">{room.total ? `${t.num((room.slot ?? 0) + 1)} / ${t.num(room.total)}` : "—"}</td>
                    <td>{t.t(`admin.live.phase.${room.phase}`)}</td>
                    <td dir="ltr">{stamp(room.createdAt)}</td>
                    <td>
                      {confirming === room.id ? (
                        <div class="row" style="gap:.3rem;justify-content:flex-end">
                          <button class="btn btn--danger btn--sm" onClick={() => void end(room.id)}>{t.t("admin.live.confirmEnd")}</button>
                          <button class="btn btn--ghost btn--sm" onClick={() => setConfirming(null)}>{t.t("admin.cancel")}</button>
                        </div>
                      ) : (
                        <div class="row" style="justify-content:flex-end">
                          <button class="btn btn--danger btn--sm" onClick={() => setConfirming(room.id)}>{t.t("admin.live.end")}</button>
                        </div>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}
