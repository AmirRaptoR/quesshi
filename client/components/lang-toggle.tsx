import { useApp } from "../app";
import { LANGS } from "../i18n";

/** Three languages, so a picker rather than a flip. */
export function LangToggle() {
  const { t, setLang } = useApp();
  return (
    <div class="segmented" role="group" aria-label={t.t("lang.pick")}>
      {LANGS.map((code) => (
        <button type="button" class={`segmented__option${t.lang === code ? " is-on" : ""}`} aria-pressed={t.lang === code} onClick={() => void setLang(code)}>
          {t.t(`lang.${code}`)}
        </button>
      ))}
    </div>
  );
}
