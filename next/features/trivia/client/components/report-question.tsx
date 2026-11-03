import { useState } from "preact/hooks";
import { useApp } from "../../../../client/app";
import { post } from "../../../../client/api";

/** The server's reasons, each with today's translation key. */
const REASONS: [string, string][] = [["wrong_answer", "wronganswer"], ["unclear", "unclear"], ["duplicate", "duplicate"], ["offensive", "offensive"], ["other", "other"]];

/** A quiet way to flag a bad question: there when a player is annoyed, invisible the rest of the time. */
export function ReportQuestion({ questionId }: { questionId: string }) {
  const { me, t } = useApp();
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState(false);
  // Reporting is closed to guests on the server, so the button would only fail for them.
  if (!me || me.guest) return null;
  if (done) return <span class="faint" style="font-size:var(--step--1)">{t.t("report.thanks")}</span>;
  if (!open) return <button class="report__open" onClick={() => setOpen(true)}>{t.t("report.button")}</button>;
  const send = async (reason: string) => {
    setBusy(true);
    // Counted or a repeat from this player, the button's job is done either way.
    await post(`/api/questions/${questionId}/report`, { reason }).catch(() => null);
    setBusy(false);
    setDone(true);
  };
  return (
    <div class="report">
      <span class="faint" style="font-size:var(--step--1)">{t.t("report.why")}</span>
      <div class="report__reasons">
        {REASONS.map(([reason, key]) => (
          <button class="chip report__reason" disabled={busy} onClick={() => void send(reason)}>{t.t(`report.${key}`)}</button>
        ))}
        <button class="chip report__reason" onClick={() => setOpen(false)}>{t.t("report.cancel")}</button>
      </div>
    </div>
  );
}
