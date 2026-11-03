import { describe, expect, test } from "bun:test";
import { splitCredit } from "./media-block";

describe("a media credit", () => {
  test("shows the provider's trailing source link as a separate link", () => {
    expect(splitCredit("Photo: Ann, CC BY-SA 4.0 — https://commons.example.com/x")).toEqual({ credit: "Photo: Ann, CC BY-SA 4.0", source: "https://commons.example.com/x" });
    expect(splitCredit("Photo: Ann")).toEqual({ credit: "Photo: Ann", source: null });
  });
});
