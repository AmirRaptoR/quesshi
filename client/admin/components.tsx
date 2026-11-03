import type { ComponentChildren } from "preact";
import { useEffect, useState } from "preact/hooks";
import { useLocation } from "preact-iso";
import { ApiError } from "../api";
import { useApp } from "../app";
import { Icon } from "../components/icon";
import { LangToggle } from "../components/lang-toggle";
import { pagerSlots, pageCount, PAGE_SIZES } from "./logic";
import { adminSession, useAdminSession, type AdminMe } from "./session";
import { Version } from "../components/version";

/** Sends a request whose body is a file, as the import and media upload take it. */
export async function sendFile<T>(path: string, file: Blob): Promise<T> {
  const response = await fetch(path, { method: "POST", credentials: "same-origin", body: file });
  const data: unknown = await response.json().catch(() => null);
  if (!response.ok) {
    const code = data && typeof data === "object" && "error" in data && typeof data.error === "string" ? data.error : "common.error";
    throw new ApiError(response.status, code, data);
  }
  return data as T;
}

/** Saves what the server sends as a file, under the name it gives. */
export async function download(path: string, fallbackName: string): Promise<void> {
  const response = await fetch(path, { credentials: "same-origin" });
  if (!response.ok) throw new ApiError(response.status, "common.error", null);
  const name = /filename="([^"]+)"/.exec(response.headers.get("content-disposition") ?? "")?.[1] ?? fallbackName;
  const url = URL.createObjectURL(await response.blob());
  const link = Object.assign(document.createElement("a"), { href: url, download: name });
  link.click();
  URL.revokeObjectURL(url);
}

export const Spinner = () => {
  const { t } = useApp();
  return <div class="spinner" role="status" aria-label={t.t("common.loading")} />;
};

/** Shown instead of the page when the admin's session could not be confirmed: a retry, never a sign-out. */
export function SessionUnconfirmed({ busy, onRetry }: { busy: boolean; onRetry(): void }) {
  const { t } = useApp();
  return (
    <div class="shell">
      <div class="empty">
        <h2>{t.t("session.unconfirmed.title")}</h2>
        <p class="muted">{t.t("session.unconfirmed.body")}</p>
        <button class="btn btn--ghost" disabled={busy} onClick={onRetry}>{busy ? t.t("common.loading") : t.t("common.retry")}</button>
      </div>
    </div>
  );
}

/**
 * The guard every admin page runs through: being signed in to play grants nothing here, a visitor
 * goes to the admin sign-in, and an admin with a temporary password goes to change it first.
 */
export function AdminGate({ allowMustChange = false, children }: { allowMustChange?: boolean; children: (admin: AdminMe) => ComponentChildren }) {
  const session = useAdminSession();
  const { url, route } = useLocation();
  const mustChange = session.status === "signed_in" && session.admin.mustChangePassword && !allowMustChange;
  useEffect(() => {
    if (session.status === "signed_out") route(`/admin/login?next=${encodeURIComponent(url)}`, true);
    else if (mustChange) route("/admin/password", true);
  }, [session.status, mustChange]);
  if (session.status === "unconfirmed") return <SessionUnconfirmed busy={session.retrying} onRetry={() => void adminSession.retry()} />;
  if (session.status !== "signed_in" || mustChange) return <Spinner />;
  return <>{children(session.admin)}</>;
}

export function AdminNav() {
  const { t } = useApp();
  const { path, route } = useLocation();
  const session = useAdminSession();
  const username = session.status === "signed_in" ? session.admin.username : null;
  const link = (href: string, key: string) => {
    const active = href === "/admin" ? path === href : path === href || path.startsWith(`${href}/`);
    return <a class={`admin-nav__link${active ? " is-active" : ""}`} href={href} aria-current={active ? "page" : undefined}>{t.t(key)}</a>;
  };
  const signOut = async () => {
    await adminSession.signOut();
    route("/admin/login");
  };
  return (
    <nav class="admin-nav" aria-label={t.t("admin.dashboard")}>
      <div class="admin-nav__pages">
        {link("/admin", "admin.dashboard")}
        {link("/admin/questions", "admin.questions")}
        {link("/admin/categories", "admin.categories")}
        {link("/admin/reported", "admin.reported")}
        {link("/admin/users", "admin.users")}
        {link("/admin/live", "admin.live")}
        {link("/admin/accounts", "admin.accounts")}
      </div>
      <details class="admin-account">
        <summary class="admin-account__summary">
          <span class="admin-account__avatar" aria-hidden="true">{username ? username.slice(0, 1).toUpperCase() : "?"}</span>
          <span>{username ?? "—"}</span>
        </summary>
        <div class="admin-account__menu">
          <div class="admin-account__label">{t.format("admin.auth.signedInAs", username ?? "")}</div>
          <div class="admin-account__label"><Version class="" /></div>
          <div class="admin-account__language"><LangToggle /></div>
          <a class="admin-account__item" href="/admin/password">{t.t("admin.auth.change")}</a>
          <button type="button" class="admin-account__item admin-account__item--danger" onClick={() => void signOut()}>{t.t("admin.auth.signOut")}</button>
        </div>
      </details>
    </nav>
  );
}

/** The plain admin page: a title bar and the nav, as today's dashboard, accounts, users, reported and live pages. */
export function AdminTopbar({ title, children }: { title: string; children: ComponentChildren }) {
  return (
    <div class="shell shell--wide">
      <header class="topbar"><h1 class="topbar__title">{title}</h1></header>
      <AdminNav />
      {children}
    </div>
  );
}

/** Today's newer admin frame, used by the categories and questions pages. */
export function AdminShell({ title, children }: { title: string; children: ComponentChildren }) {
  const { t } = useApp();
  return (
    <div class="shell shell--wide admin-shell">
      <header class="admin-page-head">
        <div>
          <span class="admin-page-head__product">{t.t("app.name")}</span>
          <h1>{title}</h1>
        </div>
      </header>
      <AdminNav />
      <main class="admin-page-content">{children}</main>
    </div>
  );
}

export function AdminToolbar({ label, filters, actions }: { label: string; filters: ComponentChildren; actions?: ComponentChildren }) {
  return (
    <section class="admin-toolbar" aria-label={label}>
      <div class="admin-toolbar__filters">{filters}</div>
      {actions && <div class="admin-toolbar__actions">{actions}</div>}
    </section>
  );
}

let modalCount = 0;

export function AdminModal({ title, onClose, children }: { title: string; onClose(): void; children: ComponentChildren }) {
  const { t } = useApp();
  const [id] = useState(() => `admin-modal-${++modalCount}`);
  return (
    <div class="modal admin-modal" role="dialog" aria-modal="true" aria-labelledby={id} onClick={onClose}>
      <section class="modal__panel admin-modal__panel" onClick={(e) => e.stopPropagation()}>
        <header class="admin-modal__head">
          <h2 id={id}>{title}</h2>
          <button type="button" class="icon-hit" onClick={onClose} title={t.t("common.close")} aria-label={t.t("common.close")}><Icon name="close" /></button>
        </header>
        <div class="admin-modal__body">{children}</div>
      </section>
    </div>
  );
}

/** Page numbers for a long list. */
export function Pager({ total, page, size, onPage, onSize }: { total: number; page: number; size: number; onPage(page: number): void; onSize(size: number): void }) {
  const { t } = useApp();
  if (total <= 0) return null;
  const pages = pageCount(total, size);
  const go = (to: number) => {
    const clamped = Math.min(Math.max(to, 1), pages);
    if (clamped !== page) onPage(clamped);
  };
  return (
    <nav class="pager" aria-label={t.t("admin.pagination")}>
      <span class="pager__count faint">{t.format("admin.showing", (page - 1) * size + 1, Math.min(page * size, total), total)}</span>
      {pages > 1 && (
        <>
          <button class="pager__step" disabled={page <= 1} onClick={() => go(page - 1)} aria-label={t.t("admin.prevPage")}><span class="pager__arrow" aria-hidden="true">‹</span></button>
          {pagerSlots(page, pages).map((slot, i) => slot === null
            ? <span key={`gap-${i}`} class="pager__gap" aria-hidden="true">…</span>
            : <button key={slot} class={`pager__page${slot === page ? " is-current" : ""}`} aria-current={slot === page ? "page" : undefined}
                aria-label={t.format("admin.page", slot)} onClick={() => go(slot)}>{t.num(slot)}</button>)}
          <button class="pager__step" disabled={page >= pages} onClick={() => go(page + 1)} aria-label={t.t("admin.nextPage")}><span class="pager__arrow" aria-hidden="true">›</span></button>
        </>
      )}
      <label class="pager__size">
        <span class="faint">{t.t("admin.perPage")}</span>
        <select class="select" value={String(size)} onChange={(e) => {
          const next = Number(e.currentTarget.value);
          if (next !== size) onSize(next);
        }}>
          {PAGE_SIZES.map((option) => <option value={String(option)}>{t.num(option)}</option>)}
        </select>
      </label>
    </nav>
  );
}

let fieldCount = 0;

/** New password plus confirmation, with the rule stated up front rather than after a rejection. */
export function PasswordFields({ password, repeat, onPassword, onRepeat }: { password: string; repeat: string; onPassword(value: string): void; onRepeat(value: string): void }) {
  const { t } = useApp();
  const [id] = useState(() => ++fieldCount);
  return (
    <>
      <div class="field">
        <label for={`pw-${id}`}>{t.t("admin.auth.newPassword")}</label>
        <input id={`pw-${id}`} class="input" type="password" dir="ltr" autoComplete="new-password" value={password} onInput={(e) => onPassword(e.currentTarget.value)} />
        <span class="faint" style="font-size:var(--step--1)">{t.t("password.rules")}</span>
      </div>
      <div class="field">
        <label for={`pw2-${id}`}>{t.t("admin.auth.confirmPassword")}</label>
        <input id={`pw2-${id}`} class="input" type="password" dir="ltr" autoComplete="new-password" value={repeat} onInput={(e) => onRepeat(e.currentTarget.value)} />
      </div>
    </>
  );
}
