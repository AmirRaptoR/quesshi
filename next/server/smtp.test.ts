import { afterEach, describe, expect, test } from "bun:test";
import { mailerFromEnv } from "./smtp";

/** A minimal SMTP server that records the one message it is sent. */
function fakeSmtp() {
  const received: string[] = [];
  const server = Bun.listen<{ data: boolean; lines: string[] }>({
    hostname: "127.0.0.1", port: 0,
    socket: {
      open(socket) {
        socket.data = { data: false, lines: [] };
        socket.write("220 fake ready\r\n");
      },
      data(socket, chunk) {
        for (const line of chunk.toString().split("\r\n").filter((l, i, all) => l || i < all.length - 1)) {
          if (socket.data.data) {
            if (line === ".") {
              socket.data.data = false;
              received.push(socket.data.lines.join("\n"));
              socket.write("250 queued\r\n");
            } else socket.data.lines.push(line);
          } else if (/^(EHLO|HELO)/i.test(line)) socket.write("250 fake\r\n");
          else if (/^DATA/i.test(line)) {
            socket.data.data = true;
            socket.write("354 go on\r\n");
          } else if (/^QUIT/i.test(line)) socket.end("221 bye\r\n");
          else if (line) socket.write("250 ok\r\n");
        }
      },
    },
  });
  return { port: server.port, received, stop: () => server.stop(true) };
}

let smtp: ReturnType<typeof fakeSmtp> | undefined;
afterEach(() => smtp?.stop());

describe("sign-in mail", () => {
  test("goes out over SMTP when a host is configured", async () => {
    smtp = fakeSmtp();
    const mailer = mailerFromEnv({ SMTP_HOST: "127.0.0.1", SMTP_PORT: String(smtp.port), SMTP_TLS: "false", SMTP_FROM: "no-reply@quesshi.example.com" });

    await mailer.send({ to: "ann@example.com", subject: "Your code", text: "123456" });

    expect(smtp.received).toHaveLength(1);
    expect(smtp.received[0]).toContain("Subject: Your code");
    expect(smtp.received[0]).toContain("To: ann@example.com");
    expect(smtp.received[0]).toContain("123456");
  });

  test("is written to the log when no host is configured", async () => {
    const lines: string[] = [];
    const mailer = mailerFromEnv({}, (line) => lines.push(line));

    await mailer.send({ to: "ann@example.com", subject: "Your code", text: "123456" });

    expect(lines.join("\n")).toContain("123456");
  });
});
