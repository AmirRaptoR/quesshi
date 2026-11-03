import type { Media } from "../../../client/components/media-block";
import type { DuelSettings } from "./logic/duel-settings";

/** The trivia server's shapes, as the client reads them. */
export interface Participant { id: string; displayName: string; avatarSeed: string; guest: boolean }

export interface Category { id: string; nameFa: string; nameEn: string; nameNl: string; icon: string; color: string; langs: string[] }

export type Kind = "choice" | "order" | "map_pick";
export interface Question { id: string; categoryId: string | null; prompt: string; kind: Kind; level: number | null; media: Media | null; card: unknown }

export interface ChoiceCard { options: string[] }
export interface OrderCard { items: { id: string; text: string }[] }
export interface MapCard { baseLayer: "blank" | "borders"; shape: "country" | "point" }
export type MapTarget = { shape: "country"; countryCode: string } | { shape: "point"; latitude: number; longitude: number; radiusKm: number };

export interface Standing { player: string; score: number; place: number; outcome: "win" | "draw" | "loss" | "none" }

export interface RoomView {
  id: string;
  format: string;
  code: string;
  owner: string;
  capacity: number;
  settings: DuelSettings;
  phase: "lobby" | "running" | "ended";
  endedReason: string | null;
  lobbyEndsAt: number;
  /** Left out of live broadcasts once the game runs; the client keeps the last roster it saw. */
  participants?: Participant[];
  rematch?: { id: string; code: string };
}

export interface LivePlayer { player: string; status: "active" | "left" | "eliminated" | "done"; score: number; answered: boolean }
export interface LiveGameView {
  version: number;
  phase: "countdown" | "question" | "reveal" | "ended";
  slot: number;
  total: number;
  dueAt: number;
  questionEndsAt: number | null;
  question: Question | null;
  reveal: { answer: unknown; explanation: string | null; answers: { player: string; value: unknown; points: number }[] } | null;
  /** Left out of broadcasts during the countdown and questions, which carry the two counts instead. */
  players?: LivePlayer[];
  answeredCount?: number;
  activeCount?: number;
  standings: Standing[] | null;
  endedReason: string | null;
}
export interface LiveRoomView extends RoomView { game?: LiveGameView | null }

export interface DuelRun { player: string; answered: number; finished: boolean; score: number | null }
export interface ReviewRow {
  slot: number; questionId: string; categoryId: string | null; prompt: string; kind: Kind; level: number | null; media: Media | null; explanation: string | null;
  card: unknown; reveal: unknown; answers: { player: string; value: unknown; correct: boolean }[];
}
export interface DuelRoomView extends RoomView { total?: number; runs?: DuelRun[]; standings?: Standing[] | null; review?: ReviewRow[] | null }

export interface Served { slot: number; total: number; servedAt: number; limitMs: number; question: Question }
export interface Answered { slot: number; questionId: string; points: number; correct: boolean; reveal: unknown; explanation: string | null; runFinished: boolean; runScore: number }
