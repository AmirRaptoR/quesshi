import { useEffect, useState } from "preact/hooks";
import { ApiError, del, get, post, put } from "../api";
import { useApp } from "../app";
import { Icon } from "../components/icon";
import { AdminGate, AdminModal, AdminShell, AdminToolbar, Spinner } from "./components";
import { isEmojiIcon, translateOr } from "./logic";
import type { Category } from "./questions-shared";

/** Which categories each enabled feature offers its players, by feature name. */
type Offers = Record<string, string[]>;

const blank = (): Category => ({ id: "", nameFa: "", nameEn: "", nameNl: "", icon: "", color: "#2EC4B6", isActive: true, sortOrder: 0 });

export function AdminCategories() {
  const { t } = useApp();
  return (
    <AdminShell title={t.t("admin.categories")}>
      <AdminGate>{() => <CategoriesBody />}</AdminGate>
    </AdminShell>
  );
}

function CategoriesBody() {
  const { t } = useApp();
  const [categories, setCategories] = useState<Category[] | null>(null);
  const [offers, setOffers] = useState<Offers>({});
  const [saved, setSaved] = useState(false);
  const [editing, setEditing] = useState<Category | null>(null);
  const [known, setKnown] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = async () => {
    const response = await get<{ categories: Category[]; settings: Offers }>("/api/admin/content-settings").catch(() => null);
    setCategories(response?.categories ?? []);
    setOffers(response?.settings ?? {});
  };
  useEffect(() => {
    void load();
  }, []);
  if (!categories) return <Spinner />;

  const refusal = (failure: unknown) => {
    if (!(failure instanceof ApiError)) throw failure;
    setError(translateOr(t, `admin.err.${failure.code}`, "common.error"));
  };
  const offer = (feature: string, id: string, on: boolean) => {
    const ids = offers[feature] ?? [];
    setOffers({ ...offers, [feature]: on ? [...new Set([...ids, id])] : ids.filter((x) => x !== id) });
  };
  const saveOffers = async () => {
    setSaved(false);
    try {
      setOffers(await put<Offers>("/api/admin/content-settings", offers));
      setSaved(true);
    } catch (failure) {
      refusal(failure);
    }
  };
  const close = () => {
    setEditing(null);
    setKnown(false);
    setError(null);
  };
  const save = async () => {
    if (!editing) return;
    setError(null);
    if (!editing.nameEn.trim()) return setError(t.t("common.error"));
    if (!isEmojiIcon(editing.icon)) return setError(t.t("admin.err.bad_icon"));
    try {
      await post("/api/admin/categories", { ...editing, icon: editing.icon.trim() });
      close();
      await load();
    } catch (failure) {
      refusal(failure);
    }
  };
  const toggle = async (category: Category) => {
    try {
      await post("/api/admin/categories", { ...category, isActive: !category.isActive });
      await load();
    } catch (failure) {
      refusal(failure);
    }
  };
  const remove = async (id: string) => {
    // The server refuses while questions still point at the category, which is what we want.
    try {
      await del(`/api/admin/categories/${id}`);
      await load();
    } catch (failure) {
      refusal(failure);
    }
  };
  const field = (key: "nameFa" | "nameEn" | "nameNl" | "icon" | "color", label: string, attrs: Record<string, string> = {}) => editing && (
    <div class="field">
      <label for={`category-${key}`}>{label}</label>
      <input id={`category-${key}`} class="input" {...attrs} value={editing[key]} onChange={(e) => setEditing({ ...editing, [key]: e.currentTarget.value })} />
    </div>
  );

  return (
    <>
      <AdminToolbar label={t.t("admin.categories")}
        filters={<span class="faint">{t.num(categories.length)} {t.t("admin.categories")}</span>}
        actions={<button type="button" class="btn btn--sm" onClick={() => { close(); setEditing(blank()); }}>{t.t("admin.addCategory")}</button>} />
      <section class="card stack" style="margin:1rem 0;padding:1rem">
        <h2 style="margin:0">{t.t("admin.categoryFamilies")}</h2>
        <p class="faint">{t.t("admin.categoryFamiliesHint")}</p>
        <div class="grid-2">
          {Object.keys(offers).map((feature) => (
            <div class="stack" key={feature}>
              <strong>{translateOr(t, `admin.feature.${feature}`, feature)}</strong>
              {categories.map((category) => (
                <label class="row" key={category.id}>
                  <input type="checkbox" checked={(offers[feature] ?? []).includes(category.id)} onChange={(e) => offer(feature, category.id, e.currentTarget.checked)} /> {category.nameEn}
                </label>
              ))}
            </div>
          ))}
        </div>
        <div class="row">
          <button class="btn btn--sm" onClick={() => void saveOffers()}>{t.t("admin.saveGameCategories")}</button>
          {saved && <span class="faint">{t.t("admin.saved")}</span>}
        </div>
      </section>

      <div class="scroll-x admin-table-wrap">
        <table class="table admin-table">
          <thead>
            <tr><th>{t.t("admin.name")}</th><th>{t.t("admin.category")}</th><th>{t.t("admin.status")}</th><th class="admin-table__actions-head" /></tr>
          </thead>
          <tbody>
            {categories.map((row) => (
              <tr key={row.id}>
                <td>
                  <div class="admin-category-name">
                    <span class="admin-category-icon" style={`--category-colour:${row.color}`} aria-hidden="true">{row.icon}</span>
                    <span><strong dir="auto">{row.nameFa}</strong><small dir="ltr">{row.nameEn}</small></span>
                  </div>
                </td>
                <td><code class="admin-code">{row.id}</code></td>
                <td><span class={`admin-status ${row.isActive ? "is-active" : "is-inactive"}`}>{row.isActive ? t.t("admin.active") : t.t("admin.inactive")}</span></td>
                <td>
                  <div class="admin-row-actions">
                    <button type="button" class="btn btn--ghost btn--sm" onClick={() => { setError(null); setEditing({ ...row }); setKnown(true); }}>{t.t("admin.edit")}</button>
                    <button type="button" class="icon-hit" onClick={() => void toggle(row)}
                      title={row.isActive ? t.t("admin.deactivate") : t.t("admin.activate")} aria-label={row.isActive ? t.t("admin.deactivate") : t.t("admin.activate")}>
                      <Icon name={row.isActive ? "eye" : "eye-off"} />
                    </button>
                    <button type="button" class="icon-hit admin-danger" onClick={() => void remove(row.id)} title={t.t("admin.delete")} aria-label={t.t("admin.delete")}><Icon name="trash" /></button>
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {error && !editing && <div class="notice notice--bad" style="margin-top:.8rem">{error}</div>}

      {editing && (
        <AdminModal title={known ? t.t("admin.edit") : t.t("admin.addCategory")} onClose={close}>
          <div class="stack" style="margin-top:.9rem">
            <div class="grid-2">
              {field("nameFa", `${t.t("admin.name")} (${t.t("lang.fa")})`)}
              {field("nameEn", `${t.t("admin.name")} (${t.t("lang.en")})`, { dir: "ltr" })}
              {field("nameNl", `${t.t("admin.name")} (${t.t("lang.nl")})`, { dir: "ltr" })}
              {field("icon", t.t("admin.icon"), { dir: "ltr", inputMode: "text" })}
              {field("color", t.t("admin.colour"), { type: "color" })}
            </div>
            <p class="faint" style="margin:0;font-size:var(--step--1)">{t.t("admin.categoryHint")}</p>
            {known && <p class="faint" dir="ltr" style="margin:0;font-size:var(--step--1)">{editing.id}</p>}
            {error && <div class="notice notice--bad">{error}</div>}
            <div class="row">
              <button class="btn" onClick={() => void save()}>{t.t("admin.save")}</button>
              <button class="btn btn--ghost" onClick={close}>{t.t("admin.cancel")}</button>
            </div>
          </div>
        </AdminModal>
      )}
    </>
  );
}
