import { pollsClient } from "../features/polls/client";
import { triviaClient } from "../features/trivia/client";
import type { ClientFeature } from "./feature";

/** The enabled features' client parts, listed by hand like the server's list. */
export const clientFeatures: readonly ClientFeature[] = [triviaClient, pollsClient];
