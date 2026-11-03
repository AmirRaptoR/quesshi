import en from "./i18n/en.json";
import fa from "./i18n/fa.json";
import nl from "./i18n/nl.json";
import type { Lang } from "./identity.store";

export interface MailMessage { to: string; subject: string; text: string }

/** Sends mail. Production uses SMTP; tests capture the messages. */
export interface Mailer {
  send(message: MailMessage): Promise<void>;
}

const texts: Record<Lang, Record<string, string>> = { en, fa, nl };

/** Today's sign-in mail: title, intro, the code, and how long it lasts. */
export function signInCodeMail(to: string, code: string, lang: Lang, minutes: number): MailMessage {
  const t = texts[lang];
  return {
    to,
    subject: t["email.otp.subject"]!,
    text: [t["email.otp.title"], t["email.otp.intro"], code, t["email.otp.note"]!.replace("{0}", String(minutes))].join("\n\n"),
  };
}
