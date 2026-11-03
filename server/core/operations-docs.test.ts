import { describe, expect, test } from "bun:test";

describe("docs/operations.md", () => {
  test("explains creating the Telegram bot and Mini App, the public origin and TELEGRAM_BOT_TOKEN", async () => {
    const doc = await Bun.file(new URL("../../docs/operations.md", import.meta.url)).text();
    const section = doc.slice(doc.indexOf("## Telegram Mini App"));

    expect(section).toContain("BotFather");
    expect(section).toContain("/newbot");
    expect(section).toContain("/newapp");
    expect(section).toContain("public origin");
    expect(section).toContain("Set `TELEGRAM_BOT_TOKEN`");
  });
});
