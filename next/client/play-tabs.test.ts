import { describe, expect, test } from "bun:test";
import { pickTab } from "./play-tabs";

describe("the play hub's remembered tab", () => {
  const tabs = ["live", "offline", "voting"];

  test("a stored tab that exists comes back as itself", () => {
    expect(pickTab("offline", tabs)).toBe("offline");
    expect(pickTab("voting", tabs)).toBe("voting");
  });

  test("nothing stored opens on the first tab", () => {
    expect(pickTab(null, tabs)).toBe("live");
  });

  test("an unreadable value, or a tab no enabled feature offers, opens on the first tab", () => {
    for (const stored of ["", "duels", "Offline", "OFFLINE"]) expect(pickTab(stored, tabs)).toBe("live");
    expect(pickTab("offline", ["voting"])).toBe("voting");
  });

  test("with no tabs there is nothing to pick", () => {
    expect(pickTab("live", [])).toBeNull();
  });
});
