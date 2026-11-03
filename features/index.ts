import type { Feature } from "../server/core/feature";
import polls from "./polls";
import trivia from "./trivia";

/** The enabled features. A feature that is not listed here does not run, even if its folder exists. */
export const features: readonly Feature[] = [trivia, polls];
