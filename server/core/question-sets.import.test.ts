import { afterEach, describe, expect, test } from "bun:test";
import { choice } from "../../features/trivia/kinds/choice";
import { testApp } from "../../tests/support/app";
import { defineFeature } from "./feature";
import { MAX_SET_IMPORT_BYTES, SET_IMPORT_EXAMPLE } from "./question-input";
import { createQuestionSetsStore, MAX_QUESTIONS_PER_SET } from "./question-sets.store";

let app: ReturnType<typeof testApp>;
afterEach(() => app.cleanup());

const quiz = defineFeature({ name: "quiz", kinds: { choice } });
const row = (n: number, fields: Record<string, unknown> = {}) => ({ ...SET_IMPORT_EXAMPLE, prompt: `Question ${n}?`, ...fields });
const importInto = (cookie: string, set: string, text: string) => app.call("POST", `/api/question-sets/${set}/import`, { text }, cookie);

async function owner() {
  app = testApp([quiz]);
  const ann = await app.signIn("ann@example.com");
  const set = (await app.call("POST", "/api/question-sets", { name: "Mine", lang: "en" }, ann)).body.id as string;
  const stored = () => createQuestionSetsStore(app.world.db).questions(set);
  return { ann, set, stored };
}

const CSV_HEADER = "prompt,choice1,choice2,choice3,choice4,correctindex,level,explanation";

describe("importing questions into a set", () => {
  test("a JSON array saves the valid rows and reports the accepted count", async () => {
    const { ann, set, stored } = await owner();

    const result = await importInto(ann, set, JSON.stringify([row(1), row(2)]));

    expect(result.body).toEqual({ accepted: 2, rejected: [] });
    expect(stored().map((q) => q.prompt)).toEqual(["Question 1?", "Question 2?"]);
    expect(stored()[0]).toMatchObject({ kind: "choice", level: 1, payload: { options: ["Mars", "Venus", "Jupiter", "Mercury"], correctIndex: 0 } });
  });

  test("invalid rows are reported with their reasons and not saved", async () => {
    const { ann, set, stored } = await owner();

    const result = await importInto(ann, set, JSON.stringify([row(1), row(2, { choice2: "Mars" }), row(3), row(4, { correctindex: 4 })]));

    expect(result.body).toEqual({ accepted: 2, rejected: [{ row: 2, reason: "bad_choices" }, { row: 4, reason: "bad_correct_index" }] });
    expect(stored().map((q) => q.prompt)).toEqual(["Question 1?", "Question 3?"]);
  });

  test("a fenced array with text around it gives the same result as the bare array", async () => {
    const { ann, set } = await owner();
    const rows = JSON.stringify([row(1), row(2, { choice2: "Mars" })], null, 2);
    const bare = await importInto(ann, set, rows);

    const fenced = await importInto(ann, set, `Here are your questions:\n\n\`\`\`json\n${rows}\n\`\`\`\n\nHope that helps!`);
    const unfenced = await importInto(ann, set, `Sure! ${rows} Enjoy.`);

    expect(fenced.body).toEqual(bare.body);
    expect(unfenced.body).toEqual(bare.body);
  });

  test("CSV with the same columns gives the same result as JSON", async () => {
    const { ann, set, stored } = await owner();
    const csv = [CSV_HEADER, "Question 1?,Mars,Venus,Jupiter,Mercury,0,1,Because", "Question 2?,Mars,Mars,Jupiter,Mercury,0,1,", "Question 3?,A,B,C,D,2,3,"].join("\n");

    const result = await importInto(ann, set, `Here you go:\n\`\`\`csv\n${csv}\n\`\`\`\nDone`);
    const bare = await importInto(ann, set, csv);

    expect(result.body).toEqual({ accepted: 2, rejected: [{ row: 2, reason: "bad_choices" }] });
    expect(bare.body).toEqual(result.body);
    expect(stored()[0]).toMatchObject({ prompt: "Question 1?", explanation: "Because", payload: { correctIndex: 0 } });
  });

  test("fewer than four distinct options or an index outside 0-3 is rejected with a reason", async () => {
    const { ann, set } = await owner();

    const result = await importInto(ann, set, JSON.stringify([
      row(1, { choice4: "" }), row(2, { choice4: "mars" }), row(3, { correctindex: -1 }), row(4, { correctindex: 4 }), row(5, { correctindex: 3 }),
    ]));

    expect(result.body).toEqual({
      accepted: 1,
      rejected: [{ row: 1, reason: "bad_choices" }, { row: 2, reason: "bad_choices" }, { row: 3, reason: "bad_correct_index" }, { row: 4, reason: "bad_correct_index" }],
    });
  });

  test("a paste larger than 200 KB is refused", async () => {
    const { ann, set, stored } = await owner();
    const huge = JSON.stringify([row(1, { explanation: "x".repeat(MAX_SET_IMPORT_BYTES) })]);

    const result = await importInto(ann, set, huge);

    expect(result.status).toBe(413);
    expect(result.body).toEqual({ error: "file_too_large" });
    expect(stored()).toEqual([]);
  });

  test("rows beyond the 100-question cap are refused", async () => {
    const { ann, set, stored } = await owner();
    const rows = Array.from({ length: MAX_QUESTIONS_PER_SET - 1 }, (_, i) => row(i));
    await importInto(ann, set, JSON.stringify(rows));

    const result = await importInto(ann, set, JSON.stringify([row(200), row(201), row(202)]));

    expect(result.body).toEqual({ accepted: 1, rejected: [{ row: 2, reason: "set_full" }, { row: 3, reason: "set_full" }] });
    expect(stored()).toHaveLength(MAX_QUESTIONS_PER_SET);
  });

  test("a player cannot import into another player's set", async () => {
    const { set, stored } = await owner();
    const bob = await app.signIn("bob@example.com");

    const result = await importInto(bob, set, JSON.stringify([row(1)]));

    expect(result.status).toBe(404);
    expect(stored()).toEqual([]);
  });

  test("the prompt template's example row imports successfully", async () => {
    const { ann, set } = await owner();

    const result = await importInto(ann, set, JSON.stringify([SET_IMPORT_EXAMPLE]));

    expect(result.body).toEqual({ accepted: 1, rejected: [] });
  });
});
