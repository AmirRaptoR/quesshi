import { lazy } from "preact-iso";
import type { ClientFeature } from "../../../client/feature";
import { POLL_API } from "./api";
import { ChoiceForm, ChoicePreview, RosterForm, RosterPreview } from "./question-forms";
import { VotingTab } from "./voting-tab";

/** The polls feature's screens and what it adds to the core's: the Voting tab, its lobby and its question editors. */
export const pollsClient: ClientFeature = {
  name: "polls",
  routes: [
    { path: "/play/voting", page: lazy(() => import("./voting-settings").then((m) => m.VotingSettings)) },
    { path: "/voting/lobby/:code", page: lazy(() => import("./voting-lobby").then((m) => m.VotingLobby)) },
    { path: "/voting/:id", page: lazy(() => import("./voting").then((m) => m.Voting)) },
  ],
  lobbyPaths: { poll: (room) => `/voting/lobby/${room.code}` },
  lobbyApis: { poll: POLL_API },
  playTabs: [{ id: "voting", label: "home.modeVoting", panel: VotingTab }],
  questionForms: { poll_choice: ChoiceForm, poll_roster: RosterForm },
  questionPreviews: { poll_choice: ChoicePreview, poll_roster: RosterPreview },
};
