import type { Media } from "../../../client/components/media-block";
import type { RoomView } from "../../../client/lobby";
import type { PollOption } from "./results";

/** Where the poll format's lobby and game routes live. */
export const POLL_API = "/f/polls/poll";

export interface PollSettings { lang: string; questions: number; categoryIds: string[] }

/** The room view of a poll: the core's room plus the game's own view. */
export interface PollRoom extends RoomView {
  settings: PollSettings;
  question?: { slot: number; total: number; prompt: string; media: Media | null; options: PollOption[] } | null;
  answered?: string[];
  mine?: number | null;
  inactive?: string[];
  answers?: Record<string, number>[];
  results?: {
    slots: ({ counts: number[]; allAgreed: boolean } | null)[];
    pairs: { players: [string, string]; same: number; different: number; percent: number | null }[] | null;
    allAgreedCount: number | null;
  };
  review?: { prompt: string; media: Media | null; options: PollOption[] }[];
}

export interface PollCategory { id: string; nameFa: string; nameEn: string; nameNl: string; icon: string; langs: string[] }
