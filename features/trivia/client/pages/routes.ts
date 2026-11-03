import { Duel } from "./duel";
import { gated } from "./gate";
import { Live } from "./live";
import { Lobby } from "./lobby";
import { Play } from "./play";
import { QuestionSettings } from "./questions";

// One chunk for trivia's game screens: a player who opens one usually opens the next.
export const QuestionsPage = gated(QuestionSettings, false);
// The shared lobby gates its own visitors.
export const LobbyPage = Lobby;
export const LivePage = gated(Live, true);
export const PlayPage = gated(Play, true);
export const DuelPage = gated(Duel, true);
