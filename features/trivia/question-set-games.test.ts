import { afterEach, describe, expect, test } from "bun:test";
import { T0, testApp } from "../../tests/support/app";
import { createQuestionSetsStore, type SetQuestionInput } from "../../server/core/question-sets.store";
import trivia from ".";

let app: ReturnType<typeof testApp>;
afterEach(() => app.cleanup());

const NOW = new Date(T0).toISOString();
const choice = (id: string, level = 1): SetQuestionInput => ({ id, kind: "choice", level, prompt: `Set ${id}?`, payload: { options: ["A", "B", "C", "D"], correctIndex: 0 }, explanation: null });
const base = { lang: "en", questions: 10, categoryIds: [] as string[], levels: [] as number[], capacity: 2 };

/** A trivia app with category questions, and a player who owns a set of `size` questions. */
async function withSet(size: number) {
  app = testApp([trivia]);
  app.seedChoiceQuestions();
  const owner = await app.signIn("owner@example.com");
  const setId = (await app.call("POST", "/api/question-sets", { name: "Mine", lang: "en" }, owner)).body.id as string;
  createQuestionSetsStore(app.world.db).append(setId, Array.from({ length: size }, (_, i) => choice(`sq${i}`)), NOW);
  return { owner, setId };
}

/** Opens a lobby of the format with a second player, starts it, and returns the ids the game was dealt, in slot order. */
async function dealt(format: "live" | "duel", owner: string, settings: Record<string, unknown>) {
  const { id, code } = (await app.call("POST", `/f/trivia/${format}/lobby`, settings, owner)).body;
  await app.call("POST", `/f/trivia/${format}/join/${code}`, undefined, await app.signIn(`bo-${format}@example.com`));
  const started = await app.call("POST", `/f/trivia/${format}/${id}/start`, undefined, owner);
  expect(started.status).toBe(200);
  return app.world.db.query<{ question_id: string }, [string]>("select question_id from session_questions where session_id = ? order by slot").all(id).map((r) => r.question_id);
}

describe("games from a question set", () => {
  test("a live game created with a question set draws only that set's questions", async () => {
    const { owner, setId } = await withSet(12);

    const ids = await dealt("live", owner, { ...base, questionSetId: setId });

    expect(ids).toHaveLength(10);
    expect(ids.every((id) => id.startsWith("sq"))).toBe(true);
    expect(new Set(ids).size).toBe(10);
  });

  test("a duel room with a code created with a question set draws only that set's questions", async () => {
    const { owner, setId } = await withSet(12);

    const ids = await dealt("duel", owner, { ...base, questionSetId: setId });

    expect(ids).toHaveLength(10);
    expect(ids.every((id) => id.startsWith("sq"))).toBe(true);
  });

  test("a set with fewer questions than requested gives a game with all of the set's questions", async () => {
    const { owner, setId } = await withSet(4);

    const live = await dealt("live", owner, { ...base, questionSetId: setId });
    const duel = await dealt("duel", owner, { ...base, questionSetId: setId });

    expect([...live].sort()).toEqual(["sq0", "sq1", "sq2", "sq3"]);
    expect([...duel].sort()).toEqual(["sq0", "sq1", "sq2", "sq3"]);
  });

  test("creating a game with another player's set is refused", async () => {
    const { setId } = await withSet(5);
    const intruder = await app.signIn("intruder@example.com");

    for (const format of ["live", "duel"]) {
      const refused = await app.call("POST", `/f/trivia/${format}/lobby`, { ...base, questionSetId: setId }, intruder);
      expect(refused.status).toBe(400);
      expect(refused.body).toEqual({ error: "invalid_settings" });
    }
  });

  test("changing a lobby over to another player's set is refused too", async () => {
    const { setId } = await withSet(5);
    const intruder = await app.signIn("intruder@example.com");
    const { id } = (await app.call("POST", "/f/trivia/live/lobby", base, intruder)).body;

    expect((await app.call("PUT", `/f/trivia/live/${id}/settings`, { ...base, questionSetId: setId }, intruder)).status).toBe(400);
  });

  test("games created with categories never contain set questions", async () => {
    const { owner } = await withSet(30);

    const withCategory = await dealt("live", owner, { ...base, categoryIds: ["geo"] });
    const anyCategory = await dealt("duel", owner, base);

    expect([...withCategory, ...anyCategory].some((id) => id.startsWith("sq"))).toBe(false);
  });

  test("a set game's reveal and scoring match a category game's for the same answers", async () => {
    const { owner, setId } = await withSet(10);
    // Every question is at level one, in the bank and in the set, so only the source differs.
    app.world.db.run("update questions set level = 1 where id like 'q%'");
    const play = async (settings: Record<string, unknown>) => {
      const { id, code } = (await app.call("POST", "/f/trivia/duel/lobby", settings, owner)).body;
      await app.call("POST", `/f/trivia/duel/join/${code}`, undefined, await app.signIn(`bo-${id}@example.com`));
      await app.call("POST", `/f/trivia/duel/${id}/start`, undefined, owner);
      const results = [];
      for (let slot = 0; slot < 10; slot++) {
        await app.call("POST", `/f/trivia/duel/${id}/next`, undefined, owner);
        const { questionId, ...result } = (await app.call("POST", `/f/trivia/duel/${id}/answer`, { slot, answer: { index: slot % 2 } }, owner)).body;
        results.push(result);
      }
      return results;
    };

    const fromSet = await play({ ...base, questionSetId: setId });
    const fromCategory = await play({ ...base, categoryIds: ["geo"] });

    expect(fromSet).toEqual(fromCategory);
    expect(fromSet.map((r) => r.points)).toEqual([80, 0, 80, 0, 80, 0, 80, 0, 80, 0]);
  });
});
