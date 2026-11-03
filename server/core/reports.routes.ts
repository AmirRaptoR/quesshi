import { Elysia } from "elysia";
import type { AdminAuth } from "./admin";
import { adminOnly } from "./admin.routes";
import type { Clock } from "./clock";
import type { Player } from "./identity.store";
import type { QuestionsStore } from "./questions.store";
import type { ReportsStore } from "./reports.store";

const REASONS = ["wrong_answer", "unclear", "duplicate", "offensive", "other"];
const whole = (value: string | undefined, fallback: number) => (value && /^\d+$/.test(value) ? Number(value) : fallback);

/** Players report questions they were served, as today. */
export function reportRoutes(reports: ReportsStore, playerFor: (cookie: string | null) => Player | null, clock: Clock) {
  return new Elysia().post("/api/questions/:id/report", ({ params, body, request, set }) => {
    const player = playerFor(request.headers.get("cookie"));
    if (!player) {
      set.status = 401;
      return { error: "signed_out" };
    }
    const reason = (body as { reason?: unknown } | null)?.reason;
    if (typeof reason !== "string" || !REASONS.includes(reason)) {
      set.status = 400;
      return { error: "bad_reason" };
    }
    const result = reports.report(params.id, player.id, reason, clock.now().toISOString());
    if (result === "not_served") {
      set.status = 400;
      return { error: "not_your_question" };
    }
    // Reporting twice is not worth an error; the button is done either way.
    return { reported: true, alreadyReported: result === "already" };
  });
}

/** The admin's queue of reported questions. */
export function reportAdminRoutes(auth: AdminAuth, reports: ReportsStore, questions: QuestionsStore) {
  return adminOnly(auth, "/api/admin")
    .get("/reported", ({ query }) => {
      const { total, reports: page } = reports.reported(whole(query.skip, 0), Math.min(Math.max(whole(query.take, 25), 1), 100));
      return {
        total,
        items: page.flatMap(({ questionId, reports: complaints }) => {
          const question = questions.question(questionId);
          // Names, so the admin sees who complained rather than a row of opaque ids.
          return question ? [{ ...question, reports: complaints.map((r) => ({ ...r, displayName: r.displayName ?? r.playerId })) }] : [];
        }),
      };
    })
    .post("/questions/:id/dismiss-reports", ({ params, set }) => {
      if (!reports.dismiss(params.id)) {
        set.status = 404;
        return { error: "not_found" };
      }
      return questions.question(params.id);
    });
}
