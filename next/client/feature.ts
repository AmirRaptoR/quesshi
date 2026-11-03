import type { ComponentType } from "preact";

/** What the router hands a page: the address's named parts, such as :id. */
export interface PageProps { params: Record<string, string> }

/** A feature's client part: its pages, and what it adds to the core's screens. */
export interface ClientFeature {
  name: string;
  /** Pages by address, lazily loaded so a feature costs nothing until it is opened. */
  routes: readonly { path: string; page: ComponentType<PageProps> }[];
  /** A tab on the play hub (today's Live, Offline and Voting tabs). */
  playTabs?: readonly { id: string; label: string; panel: ComponentType }[];
}
