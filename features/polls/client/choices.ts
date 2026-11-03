/** Today's bounds on a choice poll's options; the server checks them again on save. */
export const MIN_CHOICES = 2;
export const MAX_CHOICES = 8;

export function choicesOf(payload: unknown): string[] {
  const options = (payload as { options?: unknown } | null | undefined)?.options;
  const valid = Array.isArray(options) && options.length >= MIN_CHOICES && options.length <= MAX_CHOICES && options.every((o) => typeof o === "string");
  return valid ? [...(options as string[])] : ["", ""];
}

export const addChoice = (choices: readonly string[]): string[] => (choices.length < MAX_CHOICES ? [...choices, ""] : [...choices]);

export const removeChoice = (choices: readonly string[], index: number): string[] =>
  choices.length > MIN_CHOICES ? choices.filter((_, i) => i !== index) : [...choices];

/** The categories with approved poll questions in the language. */
export const offeredIn = <C extends { langs: string[] }>(categories: readonly C[], lang: string): C[] => categories.filter((c) => c.langs.includes(lang));
