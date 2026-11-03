import { useEffect, useRef, useState } from "preact/hooks";
import { useLocation } from "preact-iso";
import { ApiError, del, get, post, put } from "../api";
import { useApp } from "../app";
import { initial, isAvatarSeed, tint, AVATAR_SEEDS } from "../avatar";
import { guestReturn, usableName } from "../join";
import { canStart, capacityFloor, isFull, isOwner, isParticipant, openSeats, rosterColumns, seatState, type RoomView } from "../lobby";
import { usePlayer } from "../player";
import { useRoom } from "../room";
import { GUEST_ROUTE_KEY, stored } from "../storage";
import { AvatarPicker } from "./avatar-picker";
import { GuestUpgrade } from "./guest-upgrade";
import { Icon } from "./icon";

export interface LobbyPageProps {
  /** Where the format's lobby routes live, such as /f/polls/poll. */
  api: string;
  code: string;
  /** This lobby's own address, which a guest's memory points at. */
  path: string;
  maxCapacity: number;
  /** Where the room's players go once the game starts. */
  gamePath(room: RoomView): string;
  /** The settings in one line, such as "10 questions · English". */
  settingsLine(room: RoomView): string;
  /** Where the owner changes the settings. */
  settingsHref(room: RoomView): string;
  /** Translation keys for the server's refusals of Start, by error code. */
  startErrors?: Readonly<Record<string, string>>;
}

/** Today's lobby: the code first, the roster as face tiles, and one button saying what happens next. */
export function LobbyPage({ api, code, path, maxCapacity, gamePath, settingsLine, settingsHref, startErrors = {} }: LobbyPageProps) {
  const { t, refresh } = useApp();
  const me = usePlayer(true);
  const { route } = useLocation();
  const [id, setId] = useState<string | null | undefined>(undefined);
  const [busy, setBusy] = useState(false);
  const [copied, setCopied] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [capacity, setCapacity] = useState("");
  const [name, setName] = useState(me?.displayName ?? "");
  const [avatar, setAvatar] = useState(me && isAvatarSeed(me.avatarSeed) ? me.avatarSeed : AVATAR_SEEDS[0]!);
  const [identitySaving, setIdentitySaving] = useState(false);
  const [identitySaved, setIdentitySaved] = useState(false);
  const [identityError, setIdentityError] = useState<string | null>(null);
  const sawLobby = useRef(false);

  useEffect(() => {
    if (!me) return;
    get<{ id: string }>(`/api/rooms/${encodeURIComponent(code)}`).then((found) => {
      // A guest belongs to one game; wandering onto another room's lobby sends them back to it.
      const remembered = stored.get(GUEST_ROUTE_KEY);
      if (me.guest && remembered && !guestReturn(true, remembered, found, path)) {
        route(remembered, true);
        return;
      }
      setId(found.id);
    }).catch((failure) => {
      if (!(failure instanceof ApiError)) throw failure;
      setId(null);
    });
  }, [me?.id, code]);

  const seated = (current: RoomView) => Boolean(me && isParticipant(current, me.id));
  const { room, reload } = useRoom<RoomView>(api, id ?? null);

  useEffect(() => {
    if (!room) return;
    setCapacity(String(room.capacity));
    // Only a change seen on this visit moves the page on; opening a started lobby shows a card instead, so Back still works.
    if (room.phase === "lobby") sawLobby.current = true;
    else if (sawLobby.current && room.phase === "running" && seated(room)) route(gamePath(room), true);
  }, [room]);

  if (!me || id === undefined || (id !== null && room === undefined)) return <div class="shell"><div class="spinner" role="status" aria-label={t.t("common.loading")} /></div>;

  // A lobby cancelled or expired while open here is gone, like one that never existed.
  if (id === null || !room || room.phase === "ended") {
    return (
      <div class="shell">
        <div class="empty">
          <p>{t.t("join.noSuchCode")}</p>
          <a class="btn btn--ghost" href="/">{t.t("common.back")}</a>
        </div>
      </div>
    );
  }

  if (room.phase !== "lobby") {
    return (
      <div class="shell">
        <div class="empty enter">
          <p>{t.t("lobby.alreadyStarted")}</p>
          <a class="btn btn--block" href={gamePath(room)}>{t.t("lobby.goToDuel")}</a>
        </div>
      </div>
    );
  }

  const owner = isOwner(room, me.id);
  const link = `${location.origin}/join/${room.code}`;
  const act = async (action: () => Promise<unknown>, onError: (failure: ApiError) => void) => {
    setBusy(true);
    setError(null);
    try {
      await action();
    } catch (failure) {
      if (!(failure instanceof ApiError)) throw failure;
      onError(failure);
    } finally {
      setBusy(false);
    }
  };

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(link);
      setCopied(true);
    } catch {
      setCopied(false);
    }
  };
  const share = async () => {
    if (!navigator.share) return copy();
    // Whether the person finishes or cancels the system's share sheet is theirs to decide.
    await navigator.share({ title: t.t("app.name"), text: t.t("lobby.shareCode"), url: link }).catch(() => {});
  };

  const changeCapacity = () => {
    const next = Number(capacity);
    if (!Number.isInteger(next) || next < capacityFloor(room) || (next > room.capacity && next > maxCapacity)) {
      setError(t.t("lobby.badCapacity"));
      return;
    }
    // The settings go along unchanged: the server reads the whole set on every change.
    void act(async () => {
      await put(`${api}/${room.id}/settings`, { ...(room.settings as object), capacity: next });
      await reload();
    }, () => setError(t.t("lobby.badCapacity")));
  };

  const start = () => void act(async () => {
    await post(`${api}/${room.id}/start`);
    await reload();
  }, (failure) => setError(t.t(startErrors[failure.code] ?? "common.error")));

  const leave = () => void act(async () => {
    if (owner) await del(`${api}/${room.id}`);
    else await post(`${api}/${room.id}/leave`);
    route("/");
  }, () => setError(t.t("common.error")));

  // A lost race for the last seat is no error: the reload shows the lobby full.
  const takeSeat = () => void act(async () => {
    await post(`${api}/join/${room.code}`).catch(() => {});
    await reload();
  }, () => {});

  const saveIdentity = async () => {
    setIdentitySaving(true);
    setIdentityError(null);
    setIdentitySaved(false);
    try {
      await put("/api/me", { displayName: name.trim(), avatarSeed: avatar, lang: t.lang });
      await refresh();
      await reload();
      setIdentitySaved(true);
    } catch (failure) {
      if (!(failure instanceof ApiError)) throw failure;
      setIdentityError(t.t("common.error"));
    } finally {
      setIdentitySaving(false);
    }
  };

  const open = openSeats(room);
  const line = settingsLine(room);

  return (
    <div class="shell">
      <header class="topbar enter">
        <a class="lobby-back" href="/play" aria-label={t.t("common.back")}><Icon name="chevron-back" size="1.4rem" /></a>
        <h1 class="topbar__title">{t.t("lobby.title")}</h1>
        <div class="topbar__spacer" />
      </header>

      <div class="enter" style="text-align:center;margin-top:.5rem">
        <p class="muted" style="margin:0;font-size:var(--step--1)">{t.t("lobby.shareCode")}</p>
        <p class="lobby-code" dir="ltr">{room.code}</p>
        <div class="row" style="justify-content:center;gap:.5rem;margin-top:.2rem">
          <button type="button" class="btn btn--ghost btn--pill" onClick={() => void copy()}>{copied ? t.t("lobby.copied") : t.t("lobby.copyLink")}</button>
          <button type="button" class="btn btn--ghost btn--pill" onClick={() => void share()}>{t.t("lobby.share")}</button>
        </div>
      </div>

      <section class="enter" style="margin-top:1.9rem">
        <div class="row row--between" style="align-items:baseline;margin-bottom:.75rem">
          <span style="font-size:var(--step-0);font-weight:700">{t.format("lobby.rosterCount", room.participants.length, room.capacity)}</span>
          {owner ? (
            <span class="row" style="gap:.35rem">
              <input class="input" style="width:6rem" type="number" min={capacityFloor(room)} max={Math.max(room.capacity, maxCapacity)}
                value={capacity} disabled={busy} onInput={(e) => setCapacity(e.currentTarget.value)} />
              <button type="button" class="btn btn--ghost btn--pill" disabled={busy} onClick={changeCapacity}>{t.t("admin.save")}</button>
              <span class="faint">{t.format("lobby.capacityRange", 2, maxCapacity)}</span>
              <a class="lobby-settings-line" href={settingsHref(room)}>{line}</a>
            </span>
          ) : (
            <span class="muted" style="font-size:var(--step--1)">{line}</span>
          )}
        </div>
        <div class="lobby-grid" style={`--lobby-cols:${rosterColumns(room.capacity)}`}>
          {room.participants.map((seat) => {
            const host = seatState(room, seat.id) === "host";
            return (
              <div class="lobby-tile" key={seat.id}>
                <span class="avatar avatar--tile" style={`background:${tint(seat.avatarSeed)}`}>{initial(seat.displayName)}</span>
                <span dir="auto" style="font-weight:700;font-size:var(--step--1)">{seat.displayName}</span>
                <span style={`font-size:var(--step--1);color:${host ? "var(--zaferan)" : "var(--sadaf-3)"}`}>{t.t(host ? "lobby.seatHost" : "lobby.seatReady")}</span>
              </div>
            );
          })}
          {open > 0 && (
            <div class="lobby-tile lobby-tile--open">
              <span class="lobby-tile__plus" aria-hidden="true">+</span>
              <span class="faint">{t.format("lobby.openSeats", open)}</span>
            </div>
          )}
        </div>
      </section>

      {me.guest && (
        <>
          <section class="card enter" style="margin-top:1.7rem">
            <div style="font-size:var(--step-0);font-weight:700">{t.t("lobby.identity")}</div>
            <div class="stack" style="margin-top:.8rem">
              <input class="input" maxLength={24} placeholder={t.t("join.yourName")} aria-label={t.t("join.yourName")} value={name} onInput={(e) => setName(e.currentTarget.value)} />
              <div class="field">
                <label>{t.t("lobby.avatarLabel")}</label>
                <AvatarPicker value={avatar} onPick={setAvatar} />
              </div>
              <button class="btn btn--block" disabled={!usableName(name) || identitySaving} onClick={() => void saveIdentity()}>
                {identitySaved ? t.t("profile.saved") : t.t("profile.save")}
              </button>
              {identityError && <div class="notice notice--bad">{identityError}</div>}
            </div>
          </section>
          <GuestUpgrade />
        </>
      )}

      {error && <div class="notice notice--bad enter">{error}</div>}

      <div class="enter" style="margin-top:1.7rem">
        {owner ? (
          <>
            <button class="btn btn--block" disabled={!canStart(room, me.id) || busy} onClick={start}>{t.t("lobby.start")}</button>
            <p class="faint" style="margin:.6rem 0 0;text-align:center;font-size:var(--step--1)">{t.t("lobby.startHint")}</p>
            <button type="button" class="lobby-quiet" disabled={busy} onClick={leave}>{t.t("lobby.leaveOwner")}</button>
          </>
        ) : seated(room) ? (
          <>
            <button class="btn btn--block" disabled>{t.t("lobby.waitingForOwner")}</button>
            <button type="button" class="lobby-quiet" disabled={busy} onClick={leave}>{t.t("lobby.leave")}</button>
          </>
        ) : (
          <>
            <button class="btn btn--block" disabled={isFull(room) || busy} onClick={takeSeat}>{t.t("lobby.takeSeat")}</button>
            {isFull(room) && <p class="faint" style="margin:.6rem 0 0;text-align:center;font-size:var(--step--1)">{t.t("lobby.invite.full")}</p>}
          </>
        )}
      </div>
    </div>
  );
}
