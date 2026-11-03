import { lazy } from "preact-iso";
import type { ClientFeature } from "../../../client/feature";
import { LiveTab, OfflineTab } from "./components/play-tabs";

// The admin's editors carry the world map; players never download them.
const admin = <K extends keyof typeof import("./components/admin")>(name: K) => lazy(() => import("./components/admin").then((m) => m[name]));

/** Trivia's screens, and what it adds to the core's: the Live and Offline tabs, its lobbies, question editors and live settings. */
export const triviaClient: ClientFeature = {
  name: "trivia",
  routes: [
    { path: "/play/questions", page: lazy(() => import("./pages/routes").then((m) => m.QuestionsPage)) },
    { path: "/sets", page: lazy(() => import("./pages/routes").then((m) => m.MySetsPage)) },
    { path: "/sets/new", page: lazy(() => import("./pages/routes").then((m) => m.NewQuestionSetPage)) },
    { path: "/sets/:id", page: lazy(() => import("./pages/routes").then((m) => m.QuestionSetPage)) },
    { path: "/lobby/:code", page: lazy(() => import("./pages/routes").then((m) => m.LobbyPage)) },
    { path: "/live/:id", page: lazy(() => import("./pages/routes").then((m) => m.LivePage)) },
    { path: "/play/:id", page: lazy(() => import("./pages/routes").then((m) => m.PlayPage)) },
    { path: "/duel/:id", page: lazy(() => import("./pages/routes").then((m) => m.DuelPage)) },
  ],
  lobbyPaths: { live: (room) => `/lobby/${room.code}`, async_duel: (room) => `/lobby/${room.code}` },
  lobbyApis: { live: "/f/trivia/live", async_duel: "/f/trivia/duel" },
  playTabs: [
    { id: "live", label: "home.tabLive", panel: LiveTab },
    { id: "offline", label: "home.tabOffline", panel: OfflineTab },
  ],
  questionForms: { choice: admin("ChoiceForm"), order: admin("OrderForm"), map_pick: admin("MapPickForm") },
  questionPreviews: { choice: admin("ChoicePreview"), order: admin("OrderPreview"), map_pick: admin("MapPickPreview") },
  adminLivePanels: [admin("LiveSwitchPanel"), admin("LobbyCapacityPanel")],
};
