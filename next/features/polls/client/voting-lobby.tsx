import { useApp } from "../../../client/app";
import { LobbyPage } from "../../../client/components/lobby-page";
import type { PageProps } from "../../../client/feature";
import type { RoomView } from "../../../client/lobby";
import { POLL_API, type PollSettings } from "./api";

/** Today's voting lobby: the shared lobby, with voting's settings sheet and its own refusal for too few questions. */
export function VotingLobby({ params }: PageProps) {
  const { t } = useApp();
  const code = (params.code ?? "").toUpperCase();
  const path = `/voting/lobby/${code}`;
  return (
    <LobbyPage api={POLL_API} code={code} path={path} maxCapacity={8}
      gamePath={(room) => `/voting/${room.id}`}
      settingsLine={(room: RoomView) => {
        const settings = room.settings as PollSettings;
        return t.format("lobby.settingsLine", settings.questions, t.t(`lang.${settings.lang}`));
      }}
      settingsHref={(room) => `/play/voting?lobby=${room.id}&return=${encodeURIComponent(path)}`}
      startErrors={{ not_enough_questions: "voting.lobby.notEnoughQuestions" }} />
  );
}
