import { useEffect, useRef, useState } from "preact/hooks";
import { useLocation } from "preact-iso";
import { useApp } from "../../../../client/app";
import { ApiError, del, get, post, put } from "../../../../client/api";
import { Icon } from "../../../../client/components/icon";
import type { PageProps } from "../../../../client/feature";
import { initial, tint } from "../logic/avatar";
import { draftLink } from "../logic/duel-settings";
import { range } from "../logic/difficulty-range";
import { canStart, capacityStepMin, expiryText, isFull, isOwner, isParticipant, openSeatCount, rosterColumns, segment, targetRoute } from "../logic/lobby";
import { roomSocket } from "../socket";
import type { DuelRoomView } from "../types";

const AVATARS = Array.from({ length: 12 }, (_, i) => `avatar-${i}`);
const looksLikeEmail = (email: string) => email.includes("@") && email.length > 4 && !email.includes(" ");

/**
 * Today's lobby for both trivia formats, "code first, faces not rows": the code leads, the roster
 * is a grid of face tiles, and one button says what happens next. The owner changes the questions
 * on the questions sheet and the seats here; a guest names themselves and may keep their account.
 */
export function Lobby({ params }: PageProps) {
  const { me, t } = useApp();
  const { route } = useLocation();
  const code = (params.code ?? "").toUpperCase();
  const [room, setRoom] = useState<DuelRoomView | null>(null);
  const [missing, setMissing] = useState(false);
  const [maxCapacity, setMaxCapacity] = useState(20);
  const [capacityDraft, setCapacityDraft] = useState(2);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const [now, setNow] = useState(Date.now());
  const meId = me?.id ?? "";
  // Only changes after the first read move the page on; a Back press into a started game shows the card instead.
  const loaded = useRef(false);

  const read = async (): Promise<DuelRoomView | null> => {
    try {
      const found = await get<{ id: string; format: string }>(`/f/trivia/code/${code}`);
      return await get<DuelRoomView>(`/f/trivia/${segment(found.format)}/${found.id}`);
    } catch (failure) {
      if (failure instanceof ApiError) return null;
      throw failure;
    }
  };
  const show = (next: DuelRoomView) => {
    setRoom((previous) => ({ ...next, participants: next.participants ?? previous?.participants ?? [] }));
    if (loaded.current && next.phase !== "lobby") route(targetRoute(next, meId), true);
  };
  const reload = async () => {
    const next = await read();
    if (next) show(next);
  };

  useEffect(() => {
    void read().then((first) => {
      if (!first) return setMissing(true);
      setRoom(first);
      setCapacityDraft(first.capacity);
      loaded.current = true;
    });
    get<{ maxCapacity: number }>("/f/trivia/limits").then((limits) => setMaxCapacity(limits.maxCapacity), () => {});
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [code]);

  // Only a seated player listens; a visitor and a game past its lobby have nothing to hear.
  const listening = room !== null && room.phase === "lobby" && isParticipant(room, meId);
  useEffect(() => {
    if (!listening || !room) return;
    const socket = roomSocket(`/f/trivia/${segment(room.format)}/${room.id}/socket`, {
      message: (data) => { if (data.type === "view") show(data.view as DuelRoomView); },
    });
    return () => socket.close();
  }, [listening, room?.id]);

  if (missing) {
    return (
      <div class="shell">
        <div class="empty">
          <p>{t.t("join.noSuchCode")}</p>
          <a class="btn btn--ghost" href="/">{t.t("common.back")}</a>
        </div>
      </div>
    );
  }
  if (!room) return <div class="shell"><div class="spinner" role="status" aria-label={t.t("common.loading")} /></div>;
  if (room.phase !== "lobby") {
    return (
      <div class="shell">
        <div class="empty enter">
          <p>{t.t("lobby.alreadyStarted")}</p>
          <a class="btn btn--block" href={targetRoute(room, meId)}>{t.t("lobby.goToDuel")}</a>
        </div>
      </div>
    );
  }

  const base = `/f/trivia/${segment(room.format)}/${room.id}`;
  const owner = isOwner(room, meId);
  const people = room.participants ?? [];
  const shareLink = `${location.origin}/join/${room.code}`;
  const settingsLine = t.format("lobby.settingsLine", room.settings.questions, t.t(`lang.${room.settings.lang}`));
  const sheet = draftLink("/play/questions", {
    lang: room.settings.lang, questions: room.settings.questions, levels: range(room.settings.levels), categoryIds: room.settings.categoryIds,
    returnTo: `/lobby/${room.code}`, lobby: { id: room.id, format: room.format },
  });
  const act = async (action: () => Promise<unknown>, failure = "common.error") => {
    setBusy(true);
    setError(null);
    try {
      await action();
      await reload();
    } catch {
      setError(t.t(failure));
    }
    setBusy(false);
  };
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(shareLink);
      setCopied(true);
    } catch {
      setCopied(false);
    }
  };
  const share = async () => {
    if (!navigator.share) return copy();
    // Finishing or cancelling the share sheet is the person's own business.
    await navigator.share({ title: t.t("app.name"), text: t.t("lobby.shareCode"), url: shareLink }).catch(() => {});
  };
  const changeCapacity = () => {
    // A lobby above today's maximum may keep or shrink its size, never grow.
    if (capacityDraft < capacityStepMin(room) || (capacityDraft > room.capacity && capacityDraft > maxCapacity)) return setError(t.t("lobby.badCapacity"));
    void act(() => put(`${base}/settings`, { ...room.settings, capacity: capacityDraft }), "lobby.badCapacity");
  };
  const leave = async () => {
    setBusy(true);
    await (owner ? del(base) : post(`${base}/leave`)).catch(() => null);
    route("/");
  };

  return (
    <div class="shell">
      <header class="topbar enter">
        <a class="lobby-back" href="/play" aria-label={t.t("common.back")}><Icon name="chevron-back" size="1.4rem" /></a>
        <h1 class="topbar__title">{t.t("lobby.title")}</h1>
        <div class="topbar__spacer" />
        {room.format === "live" && <span class="muted" style="font-size:var(--step--1)">{expiryText(room.lobbyEndsAt, now, t)}</span>}
      </header>

      <div class="enter" style="text-align:center;margin-top:.5rem">
        <p class="muted" style="margin:0;font-size:var(--step--1)">{t.t("lobby.shareCode")}</p>
        <p class="lobby-code" dir="ltr">{room.code}</p>
        <div class="row" style="justify-content:center;gap:.5rem;margin-top:.2rem">
          <button type="button" class="btn btn--ghost btn--pill" onClick={() => void copy()}>{t.t(copied ? "lobby.copied" : "lobby.copyLink")}</button>
          <button type="button" class="btn btn--ghost btn--pill" onClick={() => void share()}>{t.t("lobby.share")}</button>
        </div>
      </div>

      <section class="enter" style="margin-top:1.9rem">
        <div class="row row--between" style="align-items:baseline;margin-bottom:.75rem">
          <span style="font-size:var(--step-0);font-weight:700">{t.format("lobby.rosterCount", people.length, room.capacity)}</span>
          {owner ? (
            <>
              <span class="row" style="gap:.35rem">
                <input class="input" style="width:6rem" type="number" min={capacityStepMin(room)} max={Math.max(room.capacity, maxCapacity)}
                  value={capacityDraft} disabled={busy} onInput={(e) => setCapacityDraft(Number(e.currentTarget.value))} />
                <button type="button" class="btn btn--ghost btn--pill" disabled={busy} onClick={changeCapacity}>{t.t("admin.save")}</button>
                <span class="faint">{t.format("lobby.capacityRange", 2, maxCapacity)}</span>
              </span>
              <a class="lobby-settings-line" href={sheet}>{settingsLine}</a>
            </>
          ) : (
            <span class="muted" style="font-size:var(--step--1)">{settingsLine}</span>
          )}
        </div>
        <div class="lobby-grid" style={`--lobby-cols:${rosterColumns(room)}`}>
          {people.map((seat) => (
            <div class="lobby-tile" key={seat.id}>
              <span class="avatar avatar--tile" style={`background:${tint(seat.avatarSeed)}`}>{initial(seat.displayName)}</span>
              <span dir="auto" style="font-weight:700;font-size:var(--step--1)">{seat.displayName}</span>
              <span style={`font-size:var(--step--1);color:${seat.id === room.owner ? "var(--zaferan)" : "var(--sadaf-3)"}`}>
                {t.t(seat.id === room.owner ? "lobby.seatHost" : "lobby.seatReady")}
              </span>
            </div>
          ))}
          {openSeatCount(room) > 0 && (
            <div class="lobby-tile lobby-tile--open">
              <span class="lobby-tile__plus" aria-hidden="true">+</span>
              <span class="faint">{t.format("lobby.openSeats", openSeatCount(room))}</span>
            </div>
          )}
        </div>
      </section>

      {me?.guest && isParticipant(room, meId) && <GuestCards onSaved={reload} />}

      {error && <div class="notice notice--bad enter">{error}</div>}

      <div class="enter" style="margin-top:1.7rem">
        {owner ? (
          <>
            <button class="btn btn--block" disabled={!canStart(room, meId) || busy}
              onClick={() => void act(() => post(`${base}/start`), "common.error")}>{t.t("lobby.start")}</button>
            <p class="faint" style="margin:.6rem 0 0;text-align:center;font-size:var(--step--1)">{t.t("lobby.startHint")}</p>
            <button type="button" class="lobby-quiet" disabled={busy} onClick={() => void leave()}>{t.t("lobby.leaveOwner")}</button>
          </>
        ) : isParticipant(room, meId) ? (
          <>
            {/* Disabled rather than hidden: the same "what happens next" slot the owner's button fills. */}
            <button class="btn btn--block" disabled>{t.t("lobby.waitingForOwner")}</button>
            <button type="button" class="lobby-quiet" disabled={busy} onClick={() => void leave()}>{t.t("lobby.leave")}</button>
          </>
        ) : (
          <>
            {/* A lost race for the last seat is not an error: the re-read shows the lobby full. */}
            <button class="btn btn--block" disabled={isFull(room) || busy || !me}
              onClick={() => void act(() => post(`/f/trivia/${segment(room.format)}/join/${room.code}`).catch(() => null))}>{t.t("lobby.takeSeat")}</button>
            {isFull(room) && <p class="faint" style="margin:.6rem 0 0;text-align:center;font-size:var(--step--1)">{t.t("lobby.invite.full")}</p>}
          </>
        )}
      </div>
    </div>
  );
}

/** A guest's name and face, and keeping the account: the moment a guest cares about both. */
function GuestCards({ onSaved }: { onSaved(): Promise<void> }) {
  const { me, t, refresh } = useApp();
  const [name, setName] = useState(me?.displayName ?? "");
  const [avatar, setAvatar] = useState(AVATARS.includes(me?.avatarSeed ?? "") ? me!.avatarSeed : AVATARS[0]!);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [identityError, setIdentityError] = useState<string | null>(null);
  const [stage, setStage] = useState<"email" | "code">("email");
  const [email, setEmail] = useState("");
  const [otp, setOtp] = useState("");
  const [upgradeBusy, setUpgradeBusy] = useState(false);
  const [upgradeError, setUpgradeError] = useState<{ text: string; taken: boolean } | null>(null);
  const valid = name.trim().length >= 2 && name.trim().length <= 24;

  const save = async () => {
    setSaving(true);
    setIdentityError(null);
    setSaved(false);
    try {
      await put("/api/me", { displayName: name.trim(), avatarSeed: avatar, lang: t.lang });
      await refresh();
      setSaved(true);
      await onSaved();
    } catch {
      setIdentityError(t.t("common.error"));
    }
    setSaving(false);
  };
  const sendCode = async () => {
    setUpgradeBusy(true);
    setUpgradeError(null);
    try {
      await post("/api/auth/code", { email: email.trim(), lang: t.lang });
      setOtp("");
      setStage("code");
    } catch {
      setUpgradeError({ text: t.t("auth.badEmail"), taken: false });
    }
    setUpgradeBusy(false);
  };
  const verify = async () => {
    setUpgradeBusy(true);
    setUpgradeError(null);
    try {
      await post("/api/me/upgrade", { email: email.trim(), code: otp.trim() });
      // Signed in for real now: the guest cards go away with the guest.
      await refresh();
    } catch (failure) {
      const taken = failure instanceof ApiError && failure.code === "address_taken";
      setUpgradeError({ text: t.t(taken ? "lobby.upgrade.taken" : "auth.badCode"), taken });
    }
    setUpgradeBusy(false);
  };

  return (
    <>
      <section class="card enter" style="margin-top:1.7rem">
        <div style="font-size:var(--step-0);font-weight:700">{t.t("lobby.identity")}</div>
        <div class="stack" style="margin-top:.8rem">
          <input class="input" maxLength={24} value={name} onInput={(e) => setName(e.currentTarget.value)} placeholder={t.t("join.yourName")} aria-label={t.t("join.yourName")} />
          <div class="field">
            <label>{t.t("lobby.avatarLabel")}</label>
            <div class="picker">
              {AVATARS.map((seed) => (
                <button type="button" class={`picker__chip${avatar === seed ? " is-on" : ""}`} style={`--pick:${tint(seed)}`} aria-pressed={avatar === seed} onClick={() => setAvatar(seed)}>
                  <span class="avatar avatar--sm" style={`background:${tint(seed)}`} />
                </button>
              ))}
            </div>
          </div>
          <button class="btn btn--block" disabled={!valid || saving} onClick={() => void save()}>{t.t(saved ? "profile.saved" : "profile.save")}</button>
          {identityError && <div class="notice notice--bad">{identityError}</div>}
        </div>
      </section>
      <section class="card enter">
        <div style="font-size:var(--step-0);font-weight:700">{t.t("lobby.upgrade.title")}</div>
        <p class="faint" style="margin-top:.4rem">{t.t("lobby.upgrade.blurb")}</p>
        <div class="stack" style="margin-top:.8rem">
          {stage === "email" ? (
            <>
              <input class="input" type="email" inputMode="email" autoComplete="email" dir="ltr" value={email} onInput={(e) => setEmail(e.currentTarget.value)}
                placeholder="you@example.com" aria-label={t.t("auth.email")} />
              <button class="btn btn--block" disabled={upgradeBusy || !looksLikeEmail(email)} onClick={() => void sendCode()}>
                {t.t(upgradeBusy ? "common.loading" : "auth.send")}
              </button>
            </>
          ) : (
            <>
              <p class="faint" dir="ltr" style="margin:0">{email}</p>
              <input class="input code-input" inputMode="numeric" maxLength={6} dir="ltr" autoComplete="one-time-code" value={otp}
                onInput={(e) => setOtp(e.currentTarget.value)} aria-label={t.t("auth.code")} />
              <button class="btn btn--block" disabled={upgradeBusy || otp.length !== 6} onClick={() => void verify()}>
                {t.t(upgradeBusy ? "common.loading" : "auth.verify")}
              </button>
              <button class="btn btn--ghost btn--block btn--sm" disabled={upgradeBusy} onClick={() => setStage("email")}>{t.t("auth.back")}</button>
            </>
          )}
          {upgradeError && (
            <div class="notice notice--bad">
              {upgradeError.text}
              {upgradeError.taken && <a href="/signin">{t.t("auth.title")}</a>}
            </div>
          )}
        </div>
      </section>
    </>
  );
}
