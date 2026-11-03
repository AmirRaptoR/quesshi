import { Duel } from "./duel";
import { gated } from "./gate";
import { Live } from "./live";
import { Lobby } from "./lobby";
import { Play } from "./play";
import { QuestionSettings } from "./questions";
import { NewSetPage, SetPage, SetsPage } from "./sets";

// One chunk for trivia's game screens: a player who opens one usually opens the next.
export const QuestionsPage = gated(QuestionSettings, false);
// The shared lobby gates its own visitors.
export const LobbyPage = Lobby;
export const LivePage = gated(Live, true);
export const PlayPage = gated(Play, true);
export const DuelPage = gated(Duel, true);
// Sets are for accounts; the list page itself lets a guest in, to say so rather than bounce them.
export const MySetsPage = gated(SetsPage, true);
export const NewQuestionSetPage = gated(NewSetPage, false);
export const QuestionSetPage = gated(SetPage, false);
