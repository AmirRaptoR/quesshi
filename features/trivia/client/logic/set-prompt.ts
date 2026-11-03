import type { Lang } from "../../../../client/i18n";

export type SetDifficulty = "easy" | "mixed" | "hard";
export interface PromptInput { count: number; lang: Lang; topic: string; difficulty: SetDifficulty }

export const SET_COUNTS = { min: 10, max: 50, initial: 20 } as const;
export const DIFFICULTIES: readonly SetDifficulty[] = ["easy", "mixed", "hard"];

/** The words the template fills in, in the language the prompt is written in. */
const WORDS: Record<Lang, { language: Record<Lang, string>; difficulty: Record<SetDifficulty, string> }> = {
  en: { language: { fa: "Persian", en: "English", nl: "Dutch" }, difficulty: { easy: "easy (level 1-2)", mixed: "mixed (levels 1-5)", hard: "hard (level 4-5)" } },
  fa: { language: { fa: "فارسی", en: "انگلیسی", nl: "هلندی" }, difficulty: { easy: "آسان (سطح ۱ تا ۲)", mixed: "ترکیبی (سطح ۱ تا ۵)", hard: "سخت (سطح ۴ تا ۵)" } },
  nl: { language: { fa: "Perzisch", en: "Engels", nl: "Nederlands" }, difficulty: { easy: "makkelijk (niveau 1-2)", mixed: "gemengd (niveau 1-5)", hard: "moeilijk (niveau 4-5)" } },
};

// The JSON keys and the example row stay English in every language: the importer reads those names.
const EXAMPLE = `[
  {"prompt": "Which planet is known as the red planet?", "choice1": "Mars", "choice2": "Venus", "choice3": "Jupiter", "choice4": "Mercury", "correctindex": 0, "level": 1, "explanation": "Iron oxide on its surface makes Mars look red."}
]`;

const TEMPLATES: Record<Lang, string> = {
  en: `Write {count} multiple-choice quiz questions in {language} about: {topic}.
Difficulty: {difficulty}.

Rules:
- Each question has exactly 4 different answer options and exactly one correct option.
- Use only facts you are sure are true. Skip anything uncertain or disputed.
- The question must not give away the answer. Wrong options must be believable.
- Keep options short (a few words). Do not use "all of the above" or "none of the above".
- Put the correct option at a different position each time (spread evenly over 0, 1, 2 and 3).
- level is 1 (easy) to 5 (hard).
- explanation is one short sentence shown after the answer.

Reply with ONLY a JSON array, no other text, in exactly this format:
${EXAMPLE}`,
  fa: `{count} پرسش چهارگزینه‌ای مسابقه به زبان {language} درباره‌ی این موضوع بنویس: {topic}.
دشواری: {difficulty}.

قواعد:
- هر پرسش دقیقاً ۴ گزینه‌ی متفاوت و دقیقاً یک گزینه‌ی درست دارد.
- فقط از واقعیت‌هایی استفاده کن که از درستی‌شان مطمئنی. هر چیز نامطمئن یا مناقشه‌برانگیز را کنار بگذار.
- پرسش نباید پاسخ را لو بدهد. گزینه‌های نادرست باید باورپذیر باشند.
- گزینه‌ها کوتاه باشند (چند کلمه). از «همه‌ی موارد» یا «هیچ‌کدام» استفاده نکن.
- گزینه‌ی درست هر بار در جای متفاوتی باشد (به‌طور مساوی بین ۰ و ۱ و ۲ و ۳ پخش شود).
- level از 1 (آسان) تا 5 (سخت) است.
- explanation یک جمله‌ی کوتاه است که بعد از پاسخ نشان داده می‌شود.

فقط و فقط یک آرایه‌ی JSON پاسخ بده، بدون هیچ متن دیگری، دقیقاً با همین قالب (نام کلیدها را انگلیسی نگه دار):
${EXAMPLE}`,
  nl: `Schrijf {count} meerkeuzevragen voor een quiz in het {language} over: {topic}.
Moeilijkheid: {difficulty}.

Regels:
- Elke vraag heeft precies 4 verschillende antwoordopties en precies één juiste optie.
- Gebruik alleen feiten waarvan je zeker weet dat ze waar zijn. Sla alles over wat onzeker of omstreden is.
- De vraag mag het antwoord niet verklappen. Foute opties moeten geloofwaardig zijn.
- Houd opties kort (een paar woorden). Gebruik geen "alle bovenstaande" of "geen van bovenstaande".
- Zet de juiste optie telkens op een andere plek (gelijkmatig verdeeld over 0, 1, 2 en 3).
- level is 1 (makkelijk) tot 5 (moeilijk).
- explanation is één korte zin die na het antwoord wordt getoond.

Antwoord ALLEEN met een JSON-array, zonder andere tekst, precies in dit formaat (laat de namen van de sleutels Engels):
${EXAMPLE}`,
};

/** The prompt to paste into an AI chat, in the set's language, with the player's choices filled in. */
export function buildPrompt(input: PromptInput): string {
  const words = WORDS[input.lang];
  // One pass, so a topic that itself contains "{count}" is never filled in a second time.
  const values: Record<string, string> = {
    count: String(input.count), language: words.language[input.lang], topic: input.topic.trim(), difficulty: words.difficulty[input.difficulty],
  };
  return TEMPLATES[input.lang].replace(/\{(count|language|topic|difficulty)\}/g, (_, key: string) => values[key]!);
}
