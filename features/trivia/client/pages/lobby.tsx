import { useEffect, useState } from "preact/hooks";
import { useApp } from "../../../../client/app";
import { ApiError, get } from "../../../../client/api";
import { LobbyPage } from "../../../../client/components/lobby-page";
import type { PageProps } from "../../../../client/feature";
import type { RoomView } from "../../../../client/lobby";
import { range } from "../logic/difficulty-range";
import { draftLink, type DuelSettings } from "../logic/duel-settings";
import { segment, targetRoute } from "../logic/lobby";
import type { DuelRoomView } from "../types";

/**
 * Today's lobby for both trivia formats at /lobby/:code: the shared lobby, told which format's routes
 * to use, trivia's settings line, and the questions sheet as the place the owner changes them.
 */
export function Lobby({ params }: PageProps) {
  const { me, t } = useApp();
  const code = (params.code ?? "").toUpperCase();
  const [format, setFormat] = useState<string | null>(null);
  const [maxCapacity, setMaxCapacity] = useState(20);

  useEffect(() => {
    // An unknown code still goes to the shared lobby, which says so.
    get<{ format: string }>(`/api/rooms/${encodeURIComponent(code)}`).then(
      (room) => setFormat(room.format),
      (failure) => { if (failure instanceof ApiError) setFormat("live"); },
    );
    get<{ maxCapacity: number }>("/f/trivia/limits").then((limits) => setMaxCapacity(limits.maxCapacity), () => {});
  }, [code]);

  if (!format) return <div class="shell"><div class="spinner" role="status" aria-label={t.t("common.loading")} /></div>;
  const path = `/lobby/${code}`;
  const settingsOf = (room: RoomView) => room.settings as DuelSettings;
  return (
    <LobbyPage api={`/f/trivia/${segment(format)}`} code={code} path={path} maxCapacity={maxCapacity}
      gamePath={(room) => targetRoute(room as DuelRoomView, me?.id ?? "")}
      settingsLine={(room) => t.format("lobby.settingsLine", settingsOf(room).questions, t.t(`lang.${settingsOf(room).lang}`))}
      settingsHref={(room) => {
        const settings = settingsOf(room);
        return draftLink("/play/questions", {
          lang: settings.lang, questions: settings.questions, levels: range(settings.levels), categoryIds: settings.categoryIds,
          returnTo: path, lobby: { id: room.id, format: room.format },
        });
      }} />
  );
}
