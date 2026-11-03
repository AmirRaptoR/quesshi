import { afterEach, describe, expect, test } from "bun:test";
import { testApp } from "../../tests/support/app";
import { MIN_APPROVED_QUESTIONS_PER_LANGUAGE } from "../../server/core/questions.store";
import polls from ".";

let app: ReturnType<typeof testApp>;
afterEach(() => app.cleanup());

const settings = { lang: "en", questions: 10, categoryIds: [], capacity: 3 };

function seedPolls(kind: "poll_choice" | "poll_roster" = "poll_choice") {
  app.questions.addCategory({ id: "fun", nameFa: "سرگرمی", nameEn: "Fun", nameNl: "Plezier", icon: "😄", color: "#e76f51" });
  app.questions.setOffers("polls", ["fun"]);
  for (let i = 0; i < MIN_APPROVED_QUESTIONS_PER_LANGUAGE; i++) {
    app.questions.add({
      id: `p${i}`, kind, lang: "en", categoryId: "fun", level: null, prompt: `Poll ${i}?`,
      payload: kind === "poll_choice" ? { options: ["Tea", "Coffee"] } : {}, explanation: null, status: "approved", createdAt: "2026-11-03T12:00:00Z",
    });
  }
}

async function started(kind: "poll_choice" | "poll_roster" = "poll_choice") {
  app = testApp([polls]);
  seedPolls(kind);
  const ann = await app.signIn("ann@example.com");
  const bob = await app.signIn("bob@example.com");
  const { id, code } = (await app.call("POST", "/f/polls/poll/lobby", settings, ann)).body;
  await app.call("POST", `/f/polls/poll/join/${code}`, undefined, bob);
  await app.call("POST", `/f/polls/poll/${id}/start`, undefined, ann);
  const me = async (cookie: string) => (await app.call("GET", "/api/me", undefined, cookie)).body.id as string;
  return { id, ann, bob, annId: await me(ann), bobId: await me(bob) };
}

describe("polls", () => {
  test("a choice question offers its options and not applicable", async () => {
    const { id, ann } = await started();

    expect((await app.call("GET", `/f/polls/poll/${id}`, undefined, ann)).body.question).toMatchObject({
      slot: 0, total: 10, prompt: expect.stringMatching(/^Poll \d+\?$/), options: [{ kind: "choice", text: "Tea" }, { kind: "choice", text: "Coffee" }, { kind: "not_applicable" }],
    });
  });

  test("a roster question offers the players, several of us and none of us", async () => {
    const { id, ann, annId, bobId } = await started("poll_roster");

    expect((await app.call("GET", `/f/polls/poll/${id}`, undefined, ann)).body.question.options).toEqual([
      { kind: "participant", player: annId }, { kind: "participant", player: bobId }, { kind: "multiple" }, { kind: "none" },
    ]);
  });

  test("shows who answered but not what, and lets a player change their answer", async () => {
    const { id, ann, bob, annId } = await started();
    await app.call("POST", `/f/polls/poll/${id}/answer`, { slot: 0, option: 0 }, ann);
    await app.call("POST", `/f/polls/poll/${id}/answer`, { slot: 0, option: 1 }, ann);

    const bobSees = (await app.call("GET", `/f/polls/poll/${id}`, undefined, bob)).body;
    const annSees = (await app.call("GET", `/f/polls/poll/${id}`, undefined, ann)).body;

    expect(bobSees.answered).toEqual([annId]);
    expect(bobSees.mine).toBeNull();
    expect(annSees.mine).toBe(1);
    expect(JSON.stringify(bobSees)).not.toContain('"answers"');
  });

  test("refuses an option that does not exist", async () => {
    const { id, ann } = await started();

    expect((await app.call("POST", `/f/polls/poll/${id}/answer`, { slot: 0, option: 3 }, ann)).body).toEqual({ error: "not_accepted" });
  });

  test("at the end everyone sees every answer and the results", async () => {
    const { id, ann, bob, annId, bobId } = await started();
    for (let slot = 0; slot < 10; slot++) {
      await app.call("POST", `/f/polls/poll/${id}/answer`, { slot, option: 0 }, ann);
      await app.call("POST", `/f/polls/poll/${id}/answer`, { slot, option: slot === 0 ? 1 : 0 }, bob);
    }

    const view = (await app.call("GET", `/f/polls/poll/${id}`, undefined, ann)).body;

    expect(view).toMatchObject({ phase: "ended", endedReason: "resolved" });
    expect(view.answers[0]).toEqual({ [annId]: 0, [bobId]: 1 });
    expect(view.results.pairs).toEqual([{ players: [annId, bobId], same: 9, different: 1, percent: 90 }]);
    expect(view.results.allAgreedCount).toBe(9);
  });

  test("the open question carries its media, and only an ended poll lists every question for review", async () => {
    app = testApp([polls]);
    app.questions.addCategory({ id: "fun", nameFa: "سرگرمی", nameEn: "Fun", nameNl: "Plezier", icon: "😄", color: "#e76f51" });
    app.questions.setOffers("polls", ["fun"]);
    const media = { kind: "image" as const, url: "/media/tea.png", attribution: null };
    for (let i = 0; i < MIN_APPROVED_QUESTIONS_PER_LANGUAGE; i++) {
      app.questions.add({
        id: `p${i}`, kind: "poll_choice", lang: "en", categoryId: "fun", level: null, prompt: `Poll ${i}?`, media,
        payload: { options: ["Tea", "Coffee"] }, explanation: null, status: "approved", createdAt: "2026-11-03T12:00:00Z",
      });
    }
    const ann = await app.signIn("ann@example.com");
    const bob = await app.signIn("bob@example.com");
    const { id, code } = (await app.call("POST", "/f/polls/poll/lobby", settings, ann)).body;
    await app.call("POST", `/f/polls/poll/join/${code}`, undefined, bob);
    await app.call("POST", `/f/polls/poll/${id}/start`, undefined, ann);

    const running = (await app.call("GET", `/f/polls/poll/${id}`, undefined, ann)).body;
    expect(running.question.media).toEqual(media);
    expect(running.review).toBeUndefined();

    for (let slot = 0; slot < 10; slot++) {
      await app.call("POST", `/f/polls/poll/${id}/answer`, { slot, option: 0 }, ann);
      await app.call("POST", `/f/polls/poll/${id}/answer`, { slot, option: 1 }, bob);
    }
    const review = (await app.call("GET", `/f/polls/poll/${id}`, undefined, ann)).body.review;
    expect(review).toHaveLength(10);
    expect(review[0]).toEqual({ prompt: expect.stringMatching(/^Poll \d+\?$/), media, options: [{ kind: "choice", text: "Tea" }, { kind: "choice", text: "Coffee" }, { kind: "not_applicable" }] });
  });

  test("players see the categories polls offer", async () => {
    app = testApp([polls]);
    seedPolls();

    expect((await app.call("GET", "/f/polls/categories")).body).toMatchObject([{ id: "fun", langs: ["en"] }]);
  });

  test("a poll lobby holds at most eight players", async () => {
    app = testApp([polls]);
    const ann = await app.signIn("ann@example.com");

    expect((await app.call("POST", "/f/polls/poll/lobby", { ...settings, capacity: 9 }, ann)).body).toEqual({ error: "bad_capacity" });
  });
});

describe("polls, ended by an administrator", () => {
  test("ends without results and stops waiting for idle players", async () => {
    const { id, ann } = await started();

    await app.call("POST", `/api/admin/rooms/${id}/end`, undefined, await app.signInAdmin());

    expect((await app.call("GET", `/f/polls/poll/${id}`, undefined, ann)).body).toMatchObject({ phase: "ended", endedReason: "ended_by_admin", question: null });
    // Only the room's own eviction is left; the game's clock is gone.
    expect(app.scheduler.pending()).toBe(1);
  });
});

describe("polls across a restart", () => {
  test("a running poll carries on with the answers given so far", async () => {
    const { id, ann, bob, annId } = await started();
    await app.call("POST", `/f/polls/poll/${id}/answer`, { slot: 0, option: 1 }, ann);

    await app.restart();

    expect((await app.call("GET", `/f/polls/poll/${id}`, undefined, bob)).body).toMatchObject({ phase: "running", answered: [annId], question: { slot: 0 } });
    await app.call("POST", `/f/polls/poll/${id}/answer`, { slot: 0, option: 0 }, bob);
    expect((await app.call("GET", `/f/polls/poll/${id}`, undefined, ann)).body.question.slot).toBe(1);
  });

  test("an open lobby keeps its code", async () => {
    app = testApp([polls]);
    seedPolls();
    const ann = await app.signIn("ann@example.com");
    const { id, code } = (await app.call("POST", "/f/polls/poll/lobby", settings, ann)).body;

    await app.restart();

    expect((await app.call("POST", `/f/polls/poll/join/${code}`, undefined, await app.signIn("bob@example.com"))).body).toEqual({ id });
  });
});
