import { describe, expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import path from "node:path";
import { translator } from "../../../client/i18n";
import { agreementKey, answerText, optionText, pairAgreement } from "./results";

const en = translator("en", JSON.parse(readFileSync(path.join(import.meta.dir, "../../../client/public/i18n/en.json"), "utf8")));
const fa = translator("fa", JSON.parse(readFileSync(path.join(import.meta.dir, "../../../client/public/i18n/fa.json"), "utf8")));

describe("a closed question's agreement message", () => {
  test("two or more real answers on one option are all agreed", () => {
    expect(agreementKey({ counts: [2, 0, 1], allAgreed: true }, 2, false)).toBe("voting.agreement.allAgreed");
  });

  test("real answers that differ are disagreed", () => {
    expect(agreementKey({ counts: [1, 1, 1], allAgreed: false }, 2, false)).toBe("voting.agreement.disagreed");
  });

  test("fewer than two real answers is nothing to compare", () => {
    expect(agreementKey({ counts: [1, 0, 3], allAgreed: true }, 2, false)).toBe("voting.agreement.nothingToCompare");
    expect(agreementKey({ counts: [0, 0, 3], allAgreed: false }, 2, false)).toBe("voting.agreement.nothingToCompare");
  });

  test("the viewer's own not-applicable answer is nothing to compare", () => {
    expect(agreementKey({ counts: [2, 0, 1], allAgreed: true }, 2, true)).toBe("voting.agreement.nothingToCompare");
  });

  test("a roster question has no not-applicable option to leave out", () => {
    expect(agreementKey({ counts: [2, 0, 0, 0], allAgreed: true }, null, false)).toBe("voting.agreement.allAgreed");
  });

  test("an absent result is nothing to compare", () => {
    expect(agreementKey(null, 2, false)).toBe("voting.agreement.nothingToCompare");
  });
});

describe("a pair's agreement", () => {
  test("a percentage reads as one, in the language's digits", () => {
    expect(pairAgreement(67, en)).toBe("67% agreed");
    expect(pairAgreement(67, fa)).toContain("۶۷");
  });

  test("a pair with nothing comparable never reads as 0%", () => {
    expect(pairAgreement(null, en)).toBe("Nothing to compare");
  });
});

describe("an option's text", () => {
  const players = [{ id: "ann", displayName: "Ann" }, { id: "bob", displayName: "Bob" }];
  const three = [...players, { id: "cy", displayName: "Cy" }];

  test("a choice is its own text and a participant is their name", () => {
    expect(optionText({ kind: "choice", text: "Tea" }, players, en)).toBe("Tea");
    expect(optionText({ kind: "participant", player: "bob" }, players, en)).toBe("Bob");
  });

  test("not applicable and no one have their own words", () => {
    expect(optionText({ kind: "not_applicable" }, players, en)).toBe("Not applicable");
    expect(optionText({ kind: "none" }, players, en)).toBe("No one");
  });

  test("several of us reads as both between two players, more than one person among more", () => {
    expect(optionText({ kind: "multiple" }, players, en)).toBe("Both");
    expect(optionText({ kind: "multiple" }, three, en)).toBe("More than one person");
  });

  test("an answer is its option's text, or no answer", () => {
    const options = [{ kind: "choice" as const, text: "Tea" }, { kind: "not_applicable" as const }];
    expect(answerText(options, 0, players, en)).toBe("Tea");
    expect(answerText(options, 1, players, en)).toBe("Not applicable");
    expect(answerText(options, undefined, players, en)).toBe("No answer");
  });
});
