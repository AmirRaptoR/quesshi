import nodemailer from "nodemailer";
import type { Mailer } from "./core/mailer";

/**
 * Today's mail settings: SMTP when SMTP_HOST is set (port 587 with STARTTLS by default), otherwise
 * mail is written to the log, which is fine for development and nowhere else.
 */
export function mailerFromEnv(env: Record<string, string | undefined>, log: (line: string) => void = console.log): Mailer {
  if (!env.SMTP_HOST) {
    return { send: async (message) => log(`Mail to ${message.to}: ${message.subject}\n${message.text}`) };
  }
  const port = Number(env.SMTP_PORT ?? 587);
  const transport = nodemailer.createTransport({
    host: env.SMTP_HOST,
    port,
    // Port 465 speaks TLS from the start; others upgrade with STARTTLS unless SMTP_TLS is "false".
    secure: port === 465,
    requireTLS: env.SMTP_TLS !== "false" && port !== 465,
    ignoreTLS: env.SMTP_TLS === "false",
    ...(env.SMTP_USER ? { auth: { user: env.SMTP_USER, pass: env.SMTP_PASSWORD ?? "" } } : {}),
  });
  const from = env.SMTP_FROM ?? "no-reply@quesshi.app";
  return { send: async (message) => { await transport.sendMail({ from, to: message.to, subject: message.subject, text: message.text }); } };
}
