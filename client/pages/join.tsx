import { useEffect, useState } from "preact/hooks";
import { useLocation } from "preact-iso";
import { ApiError, get, post } from "../api";
import { useApp } from "../app";
import { Shamseh } from "../components/shamseh";
import type { PageProps } from "../feature";
import { clientFeatures } from "../features";
import { guestReturn, usableName } from "../join";
import { GUEST_ROUTE_KEY, stored } from "../storage";

const MARK = [true, null, true, null, true, null];

/** What GET /api/rooms/:code says about the room a code names. */
interface RoomByCode { id: string; code: string; format: string; phase: string; full: boolean; ownerName: string; settings: unknown }

/** The format's join route and lobby page, from the enabled feature that plays it. */
function formatOf(format: string) {
  for (const feature of clientFeatures) {
    const api = feature.lobbyApis?.[format];
    const lobby = feature.lobbyPaths?.[format];
    if (api && lobby) return { api, lobby };
  }
  return null;
}

const questionsOf = (settings: unknown): number | null => {
  const questions = (settings as { questions?: unknown } | null)?.questions;
  return typeof questions === "number" ? questions : null;
};

/** Where an invite link lands: a member takes the seat at once; anyone else may play as a guest with a name. */
export function Join({ params }: PageProps) {
  const { me, t, refresh } = useApp();
  const { route } = useLocation();
  const code = (params.code ?? "").toUpperCase();
  const [room, setRoom] = useState<RoomByCode | null>(null);
  const [loading, setLoading] = useState(true);
  const [name, setName] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    void (async () => {
      const found = await get<RoomByCode>(`/api/rooms/${encodeURIComponent(code)}`).catch((failure) => {
        if (!(failure instanceof ApiError)) throw failure;
        return null;
      });
      const format = found && formatOf(found.format);
      if (found && format) {
        const lobby = format.lobby(found);
        // A guest reopening their own link belongs back in their game, not at a second one.
        const back = guestReturn(me?.guest ?? false, stored.get(GUEST_ROUTE_KEY), found, lobby);
        if (back) {
          route(back, true);
          return;
        }
        if (me && !me.guest) {
          try {
            await post(`${format.api}/join/${found.code}`);
            route(lobby, true);
            return;
          } catch (failure) {
            if (!(failure instanceof ApiError)) throw failure;
          }
        }
      }
      setRoom(found && format ? found : null);
      setLoading(false);
    })();
  }, [code]);

  const playAsGuest = async () => {
    const format = room && formatOf(room.format);
    if (!room || !format) return;
    setBusy(true);
    setError(null);
    try {
      await post(`${format.api}/guest/${room.code}`, { name: name.trim(), lang: t.lang });
      const lobby = format.lobby(room);
      stored.set(GUEST_ROUTE_KEY, lobby);
      await refresh();
      route(lobby);
    } catch (failure) {
      if (!(failure instanceof ApiError)) throw failure;
      setBusy(false);
      setError(t.t("join.failed"));
    }
  };

  const questions = room && questionsOf(room.settings);
  const open = room?.phase === "lobby" && !room.full;

  return (
    <div class="shell">
      <div class="stack" style="margin-top:10vh">
        <div class="enter" style="text-align:center">
          <div style="width:7.5rem;margin-inline:auto"><Shamseh mine={MARK} theirs={MARK} ignite label={t.t("app.name")} /></div>
          <h1 style="margin-top:1.1rem;font-size:var(--step-2)">{t.t("app.name")}</h1>
        </div>

        {loading ? (
          <div class="spinner" role="status" aria-label={t.t("common.loading")} />
        ) : !room ? (
          <div class="card card--taq enter">
            <div class="empty">
              <p>{t.t("join.noSuchCode")}</p>
              <a class="btn btn--ghost" href="/signin">{t.t("auth.title")}</a>
            </div>
          </div>
        ) : (
          <div class="card card--taq enter">
            <div style="text-align:center">
              <h3 style="font-size:var(--step--1)">{t.t("join.invited")}</h3>
              <p style="margin:.7rem 0 0;font-size:var(--step-1);font-weight:800" dir="auto">{room.ownerName}</p>
              {questions !== null && <p class="muted" style="margin:.2rem 0 0">{t.format("join.questions", questions)}</p>}
            </div>

            {!open ? (
              <>
                <div class="notice" style="margin-top:1rem">{t.t("join.taken")}</div>
                <a class="btn btn--ghost btn--block" style="margin-top:.8rem" href="/signin">{t.t("auth.title")}</a>
              </>
            ) : (
              <div class="stack" style="margin-top:1.2rem">
                <div class="field">
                  <label for="guestname">{t.t("join.yourName")}</label>
                  <input id="guestname" class="input" maxLength={24} autoComplete="nickname" placeholder={t.t("join.namePlaceholder")}
                    value={name} onInput={(e) => setName(e.currentTarget.value)}
                    onKeyDown={(e) => { if (e.key === "Enter" && usableName(name)) void playAsGuest(); }} />
                </div>
                {error && <div class="notice notice--bad">{error}</div>}
                <button class="btn btn--block" disabled={busy || !usableName(name)} onClick={() => void playAsGuest()}>
                  {busy ? t.t("common.loading") : t.t("join.playAsGuest")}
                </button>
                <p class="faint" style="margin:-.35rem 0 0;text-align:center;font-size:var(--step--1)">{t.t("join.guestHint")}</p>
                <div class="row" style="gap:.6rem">
                  <hr class="grow" style="border:0;border-top:1px solid var(--line)" />
                  <span class="faint" style="font-size:var(--step--1)">{t.t("auth.or")}</span>
                  <hr class="grow" style="border:0;border-top:1px solid var(--line)" />
                </div>
                {/* Signing in comes back here, so the invite is not lost on the way through. */}
                <a class="btn btn--ghost btn--block" href={`/signin?next=${encodeURIComponent(`/join/${code}`)}`}>{t.t("join.signInInstead")}</a>
              </div>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
