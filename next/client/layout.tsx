import type { ComponentChildren } from "preact";
import { useLocation } from "preact-iso";
import { useApp } from "./app";

/** Pages with their own way out, and screens that need the whole height, as today. */
const TAB_BAR_HIDDEN = ["/play/", "/live/", "/voting/", "/settings", "/lobby/", "/f/"];

/** Today's main layout: the page, and the tab bar for signed-in members outside the admin. */
export function Layout({ children }: { children: ComponentChildren }) {
  const { me, t } = useApp();
  const { path } = useLocation();
  const hidden = path.startsWith("/admin") || TAB_BAR_HIDDEN.some((prefix) => path.startsWith(prefix));
  const tab = (href: string, icon: string, label: string) => (
    <a class={`tabbar__link${path === href || path.startsWith(`${href}/`) ? " active" : ""}`} href={href}>
      <span class="tabbar__icon" aria-hidden="true">{icon}</span>{t.t(label)}
    </a>
  );
  return (
    <>
      {children}
      {me && !me.guest && !hidden && (
        <nav class="tabbar" aria-label={t.t("app.name")}>
          {tab("/play", "◈", "nav.play")}
          {tab("/profile", "☾", "nav.profile")}
        </nav>
      )}
    </>
  );
}

export function NotFound() {
  const { t } = useApp();
  return (
    <div class="shell">
      <div class="empty">
        <h2>{t.t("notFound.title")}</h2>
        <p class="muted">{t.t("notFound.body")}</p>
        <a class="btn btn--ghost" href="/">{t.t("notFound.home")}</a>
      </div>
    </div>
  );
}
