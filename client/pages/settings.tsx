import { useState } from "preact/hooks";
import { useLocation } from "preact-iso";
import { ApiError, post, put } from "../api";
import { useApp, type Me } from "../app";
import { AVATAR_SEEDS, isAvatarSeed } from "../avatar";
import { AvatarPicker } from "../components/avatar-picker";
import { GuestUpgrade } from "../components/guest-upgrade";
import { Icon } from "../components/icon";
import { LANGS, type Lang } from "../i18n";
import { usePlayer } from "../player";
import { soundsOn, SOUNDS_KEY, storedSounds } from "../sounds";
import { GUEST_ROUTE_KEY, stored } from "../storage";

/** Each script reads at a different size; today's tiles even them out. */
const SCRIPT_SIZE: Record<Lang, string> = { fa: "1.25rem", en: "1.1rem", nl: "1rem" };

/** Today's settings: name, avatar and language saved together; sounds apply at once; sign out. */
export function Settings() {
  const { t } = useApp();
  // A guest may come here to keep their account; everything else here is theirs to change too.
  const me = usePlayer(true);
  if (!me) return <div class="shell"><div class="spinner" role="status" aria-label={t.t("common.loading")} /></div>;
  return <SettingsForm me={me} />;
}

function SettingsForm({ me }: { me: Me }) {
  const { t, refresh, setLang } = useApp();
  const { route } = useLocation();
  const [name, setName] = useState(me.displayName);
  const [lang, setPickedLang] = useState<Lang>(me.lang);
  const [avatar, setAvatar] = useState(isAvatarSeed(me.avatarSeed) ? me.avatarSeed : AVATAR_SEEDS[0]!);
  const [sounds, setSounds] = useState(() => soundsOn(stored.get(SOUNDS_KEY)));
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const save = async () => {
    setBusy(true);
    setSaved(false);
    setError(null);
    try {
      const updated = await put<Me>("/api/me", { displayName: name.trim(), lang, avatarSeed: avatar });
      await refresh();
      await setLang(updated.lang);
      setSaved(true);
    } catch (failure) {
      if (!(failure instanceof ApiError)) throw failure;
      setError(t.t("common.error"));
    } finally {
      setBusy(false);
    }
  };

  const toggleSounds = () => {
    stored.set(SOUNDS_KEY, storedSounds(!sounds));
    setSounds(!sounds);
  };

  const signOut = async () => {
    await post("/api/auth/sign-out").catch(() => {});
    stored.remove(GUEST_ROUTE_KEY);
    await refresh();
    route("/signin");
  };

  return (
    <div class="shell">
      <header class="topbar">
        <a class="icon-hit icon-hit--back" href={me.guest ? stored.get(GUEST_ROUTE_KEY) ?? "/" : "/profile"} aria-label={t.t("common.back")}>
          <Icon name="chevron-back" size="1.4rem" />
        </a>
        <h1 class="topbar__title">{t.t("settings.title")}</h1>
      </header>

      <section class="card card--taq enter">
        <div class="field">
          <label for="name">{t.t("profile.name")}</label>
          <input id="name" class="input" maxLength={24} dir="auto" value={name} onInput={(e) => setName(e.currentTarget.value)} />
        </div>

        <div class="field" style="margin-top:1rem">
          <label>{t.t("lobby.avatarLabel")}</label>
          <AvatarPicker value={avatar} onPick={setAvatar} />
        </div>

        <div class="field" style="margin-top:1rem">
          <label>{t.t("profile.language")}</label>
          <div class="script-tiles" role="group" aria-label={t.t("profile.language")}>
            {LANGS.map((code) => (
              <button type="button" class={`script-tile${lang === code ? " is-on" : ""}`} style={`font-size:${SCRIPT_SIZE[code]}`} aria-pressed={lang === code} onClick={() => setPickedLang(code)}>
                {t.t(`lang.${code}`)}
              </button>
            ))}
          </div>
        </div>

        <button class="btn btn--block" style="margin-top:1.3rem" disabled={busy} onClick={() => void save()}>
          {saved ? t.t("profile.saved") : t.t("profile.save")}
        </button>
        {error && <div class="notice notice--bad" style="margin-top:.7rem">{error}</div>}
      </section>

      <section class="card enter">
        <div class="row row--between">
          <span class="settings-row__label">{t.t("settings.sounds")}</span>
          <button type="button" class={`switch${sounds ? " is-on" : ""}`} role="switch" aria-checked={sounds} aria-label={t.t("settings.sounds")} onClick={toggleSounds}>
            <span class="switch__track"><span class="switch__thumb" /></span>
          </button>
        </div>
      </section>

      {me.guest && <GuestUpgrade />}

      <button class="btn btn--ghost btn--block" style="margin-top:1.4rem" onClick={() => void signOut()}>{t.t("profile.signOut")}</button>
    </div>
  );
}
