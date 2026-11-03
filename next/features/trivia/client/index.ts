import { lazy } from "preact-iso";
import type { ClientFeature } from "../../../client/feature";
import { ChoiceForm, ChoicePreview, LiveSwitchPanel, LobbyCapacityPanel, MapPickForm, MapPickPreview, OrderForm, OrderPreview } from "./components/admin";
import { LiveTab, OfflineTab } from "./components/play-tabs";

/** Trivia's screens, and what it adds to the core's: the Live and Offline tabs, its lobbies, question editors and live settings. */
export const triviaClient: ClientFeature = {
  name: "trivia",
  routes: [
    { path: "/play/questions", page: lazy(() => import("./pages/routes").then((m) => m.QuestionsPage)) },
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
  questionForms: { choice: ChoiceForm, order: OrderForm, map_pick: MapPickForm },
  questionPreviews: { choice: ChoicePreview, order: OrderPreview, map_pick: MapPickPreview },
  adminLivePanels: [LiveSwitchPanel, LobbyCapacityPanel],
};
