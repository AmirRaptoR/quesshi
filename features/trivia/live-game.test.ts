import { afterEach, describe, expect, test } from "bun:test";
import { createSessionsStore } from "../../server/core/sessions.store";
import { fakeScheduler, fixedRandom } from "../../tests/support/time";
import { tempWorld } from "../../tests/support/temp";
import { broadcastView, LiveGame, type LiveView, type TriviaItem } from "./live-game";

const T0 = 1_000_000;
const QUESTION_OPENS = T0 + 3_000;

const capital: TriviaItem = {
  id: "q-capital", categoryId: "geo", prompt: "Capital of Australia?", level: null, explanation: "Canberra was built as a compromise.",
  kind: "choice", payload: { options: ["Sydney", "Melbourne", "Canberra", "Perth"], correctIndex: 2 }, media: null,
};
const planet: TriviaItem = {
  id: "q-planet", categoryId: null, prompt: "Largest planet?", level: 3, explanation: null,
  kind: "choice", payload: { options: ["Jupiter", "Saturn", "Earth", "Mars"], correctIndex: 0 }, media: null,
};

let world: ReturnType<typeof tempWorld>;
afterEach(() => world.cleanup());

function game(players = ["ann", "bob"], items = [capital, planet]) {
  world = tempWorld();
  world.migrateCore();
  const scheduler = fakeScheduler(T0);
  const sessions = createSessionsStore(world.db);
  sessions.createLobby({ id: "s1", code: "ABCD", format: "live", createdAt: new Date(T0).toISOString() });
  sessions.start("s1", players, []);
  const views: LiveView[] = [];
  const live = new LiveGame({ sessionId: "s1", players, items, clock: scheduler.clock, schedule: scheduler.schedule, random: fixedRandom, sessions, publish: (view) => views.push(view) });
  live.start();
  const answers = () => world.db.query("select player_id, slot, value, credit, points from answers order by slot, player_id").all();
  return { live, views, last: () => views.at(-1)!, scheduler, answers };
}

describe("LiveGame", () => {
  test("counts down, then shows the first question without its answer", () => {
    const { views, scheduler, last } = game();
    expect(views[0]).toMatchObject({ phase: "countdown", dueAt: QUESTION_OPENS });

    scheduler.advanceTo(QUESTION_OPENS);

    expect(last()).toMatchObject({ phase: "question", slot: 0, total: 2, question: { id: "q-capital", categoryId: "geo", prompt: "Capital of Australia?", kind: "choice", card: { options: ["Sydney", "Melbourne", "Canberra", "Perth"] } }, reveal: null });
  });

  test("refuses a malformed answer without storing or publishing anything", () => {
    const { live, views, scheduler, answers } = game();
    scheduler.advanceTo(QUESTION_OPENS);
    const published = views.length;

    expect(live.answer("ann", 0, { index: 9 })).toEqual({ ok: false, error: "malformed_answer" });
    expect(answers()).toEqual([]);
    expect(views.length).toBe(published);
  });

  test("stores an answer at once, and shows who answered but never what, until the reveal", () => {
    const { live, scheduler, last, answers } = game();
    scheduler.advanceTo(QUESTION_OPENS + 1_000);

    expect(live.answer("ann", 0, { index: 2 })).toEqual({ ok: true, answer: { index: 2 } });

    expect(answers()).toEqual([{ player_id: "ann", slot: 0, value: '{"index":2}', credit: 1, points: 157 }]);
    expect(last().players.find((p) => p.player === "ann")).toMatchObject({ answered: true });
    expect(JSON.stringify(last())).not.toContain('"index":2');
  });

  test("reveals the answer, every player's choice and points once everyone has answered", () => {
    const { live, scheduler, last } = game();
    scheduler.advanceTo(QUESTION_OPENS + 1_000);
    live.answer("ann", 0, { index: 2 });
    live.answer("bob", 0, { index: 0 });

    expect(last()).toMatchObject({
      phase: "reveal",
      slot: 0,
      reveal: {
        answer: { correctIndex: 2, correctOption: "Canberra" },
        explanation: "Canberra was built as a compromise.",
        answers: [{ player: "ann", value: { index: 2 }, points: 157 }, { player: "bob", value: { index: 0 }, points: 0 }],
      },
    });
  });

  test("closes the round at the deadline and records who did not answer", () => {
    const { live, scheduler, last, answers } = game();
    scheduler.advanceTo(QUESTION_OPENS + 1_000);
    live.answer("ann", 0, { index: 2 });

    scheduler.advanceTo(QUESTION_OPENS + 22_000);

    expect(last().reveal?.answers).toEqual([{ player: "ann", value: { index: 2 }, points: 157 }, { player: "bob", value: null, points: 0 }]);
    expect(answers()).toContainEqual({ player_id: "bob", slot: 0, value: null, credit: 0, points: 0 });
  });

  test("ends with standings, records the result, and stops its timers", () => {
    const { live, scheduler, last } = game();
    scheduler.advanceTo(QUESTION_OPENS + 1_000);
    live.answer("ann", 0, { index: 2 });
    live.answer("bob", 0, { index: 1 });
    scheduler.advanceTo(QUESTION_OPENS + 23_000 + 1_000);
    live.answer("ann", 1, { index: 0 });
    live.answer("bob", 1, { index: 0 });
    scheduler.advanceTo(QUESTION_OPENS + 46_000);

    expect(last()).toMatchObject({ phase: "ended", standings: [{ player: "ann", place: 1, outcome: "win" }, { player: "bob", place: 2, outcome: "loss" }] });
    expect(world.db.query("select status, ended_reason from sessions where id = 's1'").get()).toEqual({ status: "ended", ended_reason: "completed" });
    expect(scheduler.pending()).toBe(0);
  });

  test("a player who leaves is no longer waited for", () => {
    const { live, scheduler, last } = game(["ann", "bob", "cy"]);
    scheduler.advanceTo(QUESTION_OPENS + 1_000);
    live.answer("ann", 0, { index: 2 });
    live.answer("bob", 0, { index: 2 });

    live.leave("cy");

    expect(last().phase).toBe("reveal");
  });
});

describe("the live view everyone is sent", () => {
  test("while a question is open, counts who answered instead of listing every player", () => {
    const { live, scheduler, last } = game(["ann", "bob", "cy"]);
    scheduler.advanceTo(QUESTION_OPENS + 1_000);
    live.answer("ann", 0, { index: 2 });

    const short = broadcastView(last());

    expect(short).toMatchObject({ phase: "question", answeredCount: 1, activeCount: 3, question: { prompt: "Capital of Australia?" } });
    expect(short).not.toHaveProperty("players");
  });

  test("at the reveal, carries every player's score", () => {
    const { live, scheduler, last } = game();
    scheduler.advanceTo(QUESTION_OPENS + 1_000);
    live.answer("ann", 0, { index: 2 });
    live.answer("bob", 0, { index: 0 });

    expect(broadcastView(last())).toMatchObject({ phase: "reveal", players: [{ player: "ann" }, { player: "bob" }] });
  });
});
