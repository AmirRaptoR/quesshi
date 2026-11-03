import type { ComponentType } from "preact";

/** What the router hands a page: the address's named parts, such as :id. */
export interface PageProps { params: Record<string, string> }

/** The admin's editor for one question kind's payload; the server checks it on save. */
export interface PayloadFormProps { payload: unknown; onChange(payload: unknown): void }

/** A feature's client part: its pages, and what it adds to the core's screens. */
export interface ClientFeature {
  name: string;
  /** Pages by address, lazily loaded so a feature costs nothing until it is opened. */
  routes: readonly { path: string; page: ComponentType<PageProps> }[];
  /** Where someone holding a game code goes, by the room's format (rooms of every feature share one set of codes). */
  lobbyPaths?: Readonly<Record<string, (room: { id: string; code: string }) => string>>;
  /** Where each format's lobby routes live on the server (such as /f/polls/poll), so the join page can take a seat by code. */
  lobbyApis?: Readonly<Record<string, string>>;
  /** A tab on the play hub (today's Live, Offline and Voting tabs). */
  playTabs?: readonly { id: string; label: string; panel: ComponentType }[];
  /** The admin's payload editor for each of the feature's question kinds, by kind id. */
  questionForms?: Readonly<Record<string, ComponentType<PayloadFormProps>>>;
  /** How the admin's question list shows a payload of each kind, by kind id. */
  questionPreviews?: Readonly<Record<string, ComponentType<{ payload: unknown }>>>;
  /** Panels the feature adds to the admin's live page (trivia: live on or off, largest lobby). */
  adminLivePanels?: readonly ComponentType[];
}
