import { describe, expect, test } from "bun:test";
import { ApiError } from "../api";
import { translator } from "../i18n";
import {
  adminNext, clampPage, isEmojiIcon, isPlaceholderEmail, pageAfterResize, pagerSlots, pageCount, problemsText, readPaging,
  readQuestionFilters, questionFilterSearch, questionsApiQuery, reportLabel, reportReasonKey, sessionOutcome, translateOr,
} from "./logic";

const en = translator("en", {
  "app.dir": "ltr", "admin.err.bad_icon": "One emoji.", "common.error": "Something went wrong.",
  "password.tooShort": "Too short.", "password.tooObvious": "Too obvious.", "admin.auth.wrongCurrent": "Wrong.",
  "admin.reportCount": "{0} reports", "admin.reportCountOne": "1 report",
});

describe("after signing in to the admin", () => {
  test("goes back to the admin page asked for, and nowhere outside the admin", () => {
    expect(adminNext("/admin/questions?status=pending")).toBe("/admin/questions?status=pending");
    expect(adminNext("/play")).toBe("/admin");
    expect(adminNext("https://evil.example.com/admin")).toBe("/admin");
    expect(adminNext(undefined)).toBe("/admin");
  });
});

describe("the identity check", () => {
  test("a 401 is the only answer that means signed out", () => {
    expect(sessionOutcome(new ApiError(401, "signed_out", null))).toBe("signed_out");
  });

  test("a server error keeps the session and offers a retry", () => {
    expect(sessionOutcome(new ApiError(500, "common.error", null))).toBe("unconfirmed");
  });

  test("any other refusal keeps the session", () => {
    for (const status of [403, 404, 408, 429]) expect(sessionOutcome(new ApiError(status, "x", null))).toBe("unconfirmed");
  });

  test("a network failure or an unreadable body keeps the session", () => {
    expect(sessionOutcome(new TypeError("Failed to fetch"))).toBe("unconfirmed");
    expect(sessionOutcome(new SyntaxError("Unexpected token"))).toBe("unconfirmed");
  });
});

describe("the pager", () => {
  test("counts pages, with one page for an empty list", () => {
    expect(pageCount(0, 25)).toBe(1);
    expect(pageCount(25, 25)).toBe(1);
    expect(pageCount(26, 25)).toBe(2);
  });

  test("shows every page of a short list", () => {
    expect(pagerSlots(2, 3)).toEqual([1, 2, 3]);
  });

  test("shows the first, the last and a window around the current page, with gaps between", () => {
    expect(pagerSlots(5, 10)).toEqual([1, null, 4, 5, 6, null, 10]);
  });

  test("leaves no gap where the window touches an end", () => {
    expect(pagerSlots(1, 10)).toEqual([1, 2, null, 10]);
    expect(pagerSlots(3, 10)).toEqual([1, 2, 3, 4, null, 10]);
    expect(pagerSlots(10, 10)).toEqual([1, null, 9, 10]);
  });

  test("a page past the end, after the last row went, falls back to the last page", () => {
    expect(clampPage(3, 50, 25)).toBe(2);
    expect(clampPage(2, 0, 25)).toBe(1);
    expect(clampPage(2, 60, 25)).toBe(2);
  });

  test("a new page size keeps the first row of the current page in view", () => {
    expect(pageAfterResize(3, 25, 50)).toBe(2);
    expect(pageAfterResize(2, 50, 25)).toBe(3);
    expect(pageAfterResize(1, 100, 25)).toBe(1);
  });
});

describe("paging and filters in the address", () => {
  test("read the page and a size of 25, 50 or 100, else the defaults", () => {
    expect(readPaging({ page: "3", size: "50" })).toEqual({ page: 3, size: 50 });
    expect(readPaging({ page: "0", size: "40" })).toEqual({ page: 1, size: 25 });
    expect(readPaging({ page: "x" })).toEqual({ page: 1, size: 25 });
  });

  test("read the question filters, with an unreadable level as all levels", () => {
    expect(readQuestionFilters({ lang: "fa", category: "history", status: "pending", kind: "choice", level: "3", q: "shah", page: "2" }))
      .toEqual({ lang: "fa", category: "history", status: "pending", kind: "choice", level: 3, text: "shah", page: 2, size: 25 });
    expect(readQuestionFilters({ level: "nine" })).toMatchObject({ lang: "", level: 0, page: 1 });
  });

  test("write only what differs from the defaults, so a plain view keeps a plain address", () => {
    const filters = readQuestionFilters({});
    expect(questionFilterSearch(filters)).toBe("");
    expect(questionFilterSearch({ ...filters, status: "pending", text: " a b ", page: 2, size: 50 })).toBe("status=pending&q=a+b&page=2&size=50");
  });

  test("ask the server for the page, with only the filters that are set", () => {
    const filters = readQuestionFilters({});
    expect(questionsApiQuery(filters)).toBe("skip=0&take=25");
    expect(questionsApiQuery({ ...filters, lang: "en", category: "geo", kind: "order", level: 2, text: " x ", page: 3, size: 50 }))
      .toBe("skip=100&take=50&lang=en&category=geo&kind=order&level=2&text=x");
  });
});

describe("messages", () => {
  test("a translation, or the fallback when the language has no such text", () => {
    expect(translateOr(en, "admin.err.bad_icon", "common.error")).toBe("One emoji.");
    expect(translateOr(en, "admin.err.bad_size", "common.error")).toBe("Something went wrong.");
  });

  test("a weak password lists each problem; another refusal names its reason", () => {
    expect(problemsText(en, new ApiError(400, "admin.auth.weak", { error: "admin.auth.weak", problems: ["password.tooShort", "password.tooObvious"] }))).toBe("Too short. Too obvious.");
    expect(problemsText(en, new ApiError(400, "admin.auth.wrongCurrent", { error: "admin.auth.wrongCurrent" }))).toBe("Wrong.");
  });

  test("one report reads in the singular", () => {
    expect(reportLabel(en, 1)).toBe("1 report");
    expect(reportLabel(en, 3)).toBe("3 reports");
  });

  test("a report's reason uses today's text key", () => {
    expect(reportReasonKey("wrong_answer")).toBe("report.wronganswer");
    expect(reportReasonKey("offensive")).toBe("report.offensive");
  });

  test("the bootstrap account's address, which no mail reaches, is a placeholder", () => {
    expect(isPlaceholderEmail("admin@quesshi.local")).toBe(true);
    expect(isPlaceholderEmail("root@localhost")).toBe(true);
    expect(isPlaceholderEmail("nobody")).toBe(true);
    expect(isPlaceholderEmail("admin@example.com")).toBe(false);
  });
});

describe("a category's icon", () => {
  test("is one complete emoji sequence", () => {
    for (const value of ["🍳", "🇳🇱", "👨‍👩‍👧‍👦", "✈️", "👍🏽", "1️⃣", "#️⃣", "*️⃣"]) expect(isEmojiIcon(value)).toBe(true);
  });

  test("is not missing, plain text, a symbol, several emoji or a broken sequence", () => {
    for (const value of ["", " ", "A", "icon", "◆", "→", "⌘", "😀😃", "🏽", "️", "🇳", "👨‍"]) expect(isEmojiIcon(value)).toBe(false);
  });

  test("may have spaces around it", () => {
    expect(isEmojiIcon("  👍🏽  ")).toBe(true);
  });
});
