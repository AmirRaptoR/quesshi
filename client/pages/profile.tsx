import { useState } from "preact/hooks";
import { ApiError, put } from "../api";
import { useApp } from "../app";
import { initial, tint } from "../avatar";
import { Icon } from "../components/icon";
import { usePlayer } from "../player";
import { usableName } from "../join";

/** Today's profile, for the first release: who you are, your name edited in place, and the gear to settings. */
export function Profile() {
  const { t, refresh } = useApp();
  const me = usePlayer();
  const [editing, setEditing] = useState(false);
  const [name, setName] = useState("");
  const [saving, setSaving] = useState(false);
  const [toast, setToast] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  if (!me) return <div class="shell"><div class="spinner" role="status" aria-label={t.t("common.loading")} /></div>;

  const start = () => {
    setName(me.displayName);
    setEditing(true);
    setError(null);
  };
  const save = async () => {
    if (saving || !usableName(name)) return;
    setSaving(true);
    setError(null);
    try {
      // The avatar is left as it is.
      await put("/api/me", { displayName: name.trim(), lang: me.lang });
      await refresh();
      setEditing(false);
      setToast(t.t("profile.saved"));
    } catch (failure) {
      if (!(failure instanceof ApiError)) throw failure;
      setError(t.t("common.error"));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div class="shell">
      <div class="row" style="justify-content:flex-end">
        <a class="icon-hit" href="/settings" aria-label={t.t("settings.title")}><Icon name="gear" size="1.4rem" /></a>
      </div>

      <div class="profile-id enter">
        <span class="avatar profile-id__avatar" style={`background:${tint(me.avatarSeed)}`}>{initial(me.displayName)}</span>
        {editing ? (
          <div class="profile-name-editor">
            <input class="input" maxLength={24} dir="auto" aria-label={t.t("profile.name")} value={name} onInput={(e) => setName(e.currentTarget.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter") void save();
                else if (e.key === "Escape") setEditing(false);
              }} />
            <div class="row" style="justify-content:center;gap:.45rem">
              <button type="button" class="btn btn--sm" disabled={saving || !usableName(name)} onClick={() => void save()}>{t.t("profile.save")}</button>
              <button type="button" class="btn btn--ghost btn--sm" disabled={saving} onClick={() => setEditing(false)}>{t.t("admin.cancel")}</button>
            </div>
          </div>
        ) : (
          <div class="profile-name-line">
            <h1 class="profile-id__name" dir="auto">{me.displayName}</h1>
            <button type="button" class="icon-hit" title={t.t("profile.editName")} aria-label={t.t("profile.editName")} onClick={start}><Icon name="edit" /></button>
          </div>
        )}
      </div>

      {!me.guest && <a class="btn btn--ghost btn--block" style="margin-top:1rem" href="/sets">{t.t("profile.sets")}</a>}

      {toast &&<div class="notice notice--good enter" style="margin-top:.8rem">{toast}</div>}
      {error && <div class="notice notice--bad enter" style="margin-top:.8rem">{error}</div>}
    </div>
  );
}
