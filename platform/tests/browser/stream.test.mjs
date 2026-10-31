import assert from 'node:assert/strict';
import { after, before, test } from 'node:test';
import { chromium } from 'playwright';
import { capturePageErrors, startPublishedPlatformHost } from './src/browser-harness.mjs';

const publishedDirectory = process.env.PLATFORM_BROWSER_HOST_DIRECTORY;
const runsPublishedClient = publishedDirectory !== undefined;
const sessionA = '3f0f6d3c-7c4b-4a0a-9a52-1d1f6a3b0001';
const sessionB = '3f0f6d3c-7c4b-4a0a-9a52-1d1f6a3b0002';

class StreamServer {
  requests = [];
  open = new Set();
  constructor(respond) {
    this.respond = respond;
  }

  handle = (request, response) => {
    const record = { url: request.url, build: request.headers['x-client-build'], closed: false, at: Date.now(), response };
    this.requests.push(record);
    this.open.add(record);
    response.on('close', () => {
      record.closed = true;
      this.open.delete(record);
    });
    this.respond(record, response, this.requests.length);
  };
}

function eventStream(response, ...frames) {
  response.writeHead(200, { 'content-type': 'text/event-stream', 'cache-control': 'no-cache' });
  response.write(': open\n\n');
  for (const frame of frames) response.write(frame);
}

function view(version) {
  return `event: view\ndata: {"version":${version}}\n\n`;
}

function tenant(name, stream, extra = {}) {
  return {
    config: {
      name,
      languages: [{ code: 'en', displayName: 'English', direction: 'ltr' }],
      defaultLanguage: 'en',
      capabilities: {},
      configVersion: 1,
      serverBuildId: 'test'
    },
    theme: ':root { --shab: #114477; }',
    handlers: { '/api/stream': stream.handle },
    resources: {
      '/api/me': { body: '{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","displayName":"Ada","avatarSeed":"avatar-7","lang":"en"}', type: 'application/json', tag: 'player' },
      '/api/antiforgery': { body: '{"token":"csrf"}', type: 'application/json', tag: 'csrf' },
      '/api/auth/sign-out': { body: '{}', type: 'application/json', tag: 'sign-out' },
      ...extra
    }
  };
}

async function until(condition, message, timeout = 10000) {
  const deadline = Date.now() + timeout;
  while (!condition()) {
    if (Date.now() > deadline) assert.fail(message);
    await new Promise(resolve => setTimeout(resolve, 25));
  }
}

const pause = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));

let browser;

before(async () => {
  browser = await chromium.launch({ headless: true });
});

after(async () => {
  await browser.close();
});

async function withShell(host, stream, body) {
  const context = await browser.newContext();
  const page = await context.newPage();
  const assertNoPageErrors = capturePageErrors(page);
  try {
    await body(page, host);
    assertNoPageErrors();
  } finally {
    await context.close();
    await host.close();
  }
}

async function startHost(stream, extra) {
  return startPublishedPlatformHost({ directory: publishedDirectory, tenants: { 'stream.localhost': tenant('Stream', stream, extra) } });
}

test('signed-in shell keeps one stream open across Home and Profile', { skip: !runsPublishedClient }, async () => {
  const stream = new StreamServer((record, response) => eventStream(response, view(1)));
  await withShell(await startHost(stream), stream, async (page, host) => {
    await page.goto(host.tenantUrl('stream.localhost'));
    await page.locator('#home-title').waitFor();
    await until(() => stream.requests.length === 1, 'The signed-in shell should open the stream.');
    assert.equal(stream.requests[0].url, '/api/stream');
    assert.ok(stream.requests[0].build, 'The stream request should carry the client build id.');

    await page.locator('#home-title ~ button').click();
    await page.locator('#profile-title').waitFor();
    await page.goBack();
    await page.locator('#home-title').waitFor();
    await pause(500);

    assert.equal(stream.requests.length, 1);
    assert.equal(stream.open.size, 1);
  });
});

test('changing the watched session closes the old reader before opening one for the new session', { skip: !runsPublishedClient }, async () => {
  const stream = new StreamServer((record, response) => eventStream(response, view(1)));
  await withShell(await startHost(stream), stream, async (page, host) => {
    // Page-side order of the reader cancellation versus the next stream fetch; network events race, this does not.
    await page.addInitScript(() => {
      window.streamEvents = [];
      const streamReaders = new WeakSet();
      const nativeFetch = window.fetch;
      window.fetch = (input, init) => {
        const url = typeof input === 'string' ? input : input.url;
        if (!url.includes('/api/stream')) return nativeFetch(input, init);
        window.streamEvents.push(`open ${url.split('session=')[1]}`);
        // Only readers of the stream response count; other requests (Home's data loads) cancel readers too.
        return nativeFetch(input, init).then(response => {
          const body = response.body;
          const getReader = body.getReader;
          body.getReader = function (...args) {
            const reader = getReader.apply(this, args);
            streamReaders.add(reader);
            return reader;
          };
          return response;
        });
      };
      const nativeCancel = ReadableStreamDefaultReader.prototype.cancel;
      ReadableStreamDefaultReader.prototype.cancel = function (...args) {
        if (streamReaders.has(this)) window.streamEvents.push('cancel');
        return nativeCancel.apply(this, args);
      };
    });
    await page.goto(host.tenantUrl('stream.localhost', `/?session=${sessionA}`));
    await page.locator('#home-title').waitFor();
    await until(() => stream.requests.length === 1, 'The first session stream should open.');
    assert.equal(stream.requests[0].url, `/api/stream?session=${sessionA}`);

    await page.evaluate(session => window.Blazor.navigateTo(`/?session=${session}`), sessionB);
    await until(() => stream.requests.length === 2, 'The new session stream should open.');
    await pause(500);

    const all = await page.evaluate(() => window.streamEvents);
    const events = all.slice(all.indexOf(`open ${sessionA}`));
    assert.deepEqual(
      events,
      [`open ${sessionA}`, 'cancel', `open ${sessionB}`],
      'The old reader should be cancelled before the new request opens.');

    assert.equal(stream.requests.length, 2);
    assert.equal(stream.requests[1].url, `/api/stream?session=${sessionB}`);
    assert.equal(stream.requests[0].closed, true, 'The old reader should be cancelled.');
    assert.equal(stream.open.size, 1);
  });
});

test('opening the session page watches that session and returning home clears the watch', { skip: !runsPublishedClient }, async () => {
  const stream = new StreamServer((record, response) => eventStream(response, view(1)));
  await withShell(await startHost(stream), stream, async (page, host) => {
    await page.goto(host.tenantUrl('stream.localhost', `/s/${sessionA}`));
    await until(() => stream.requests.length === 1, 'The session page should open a stream for its session.');
    assert.equal(stream.requests[0].url, `/api/stream?session=${sessionA}`);

    await page.evaluate(() => window.Blazor.navigateTo('/'));
    await page.locator('#home-title').waitFor();
    await until(() => stream.requests.length === 2, 'Returning home should reopen the stream without a session.');
    await pause(500);

    assert.equal(stream.requests[1].url, '/api/stream');
    assert.equal(stream.requests[0].closed, true, 'The session reader should be cancelled.');
    assert.equal(stream.open.size, 1);
  });
});

test('signing out cancels the stream without opening another request', { skip: !runsPublishedClient }, async () => {
  const stream = new StreamServer((record, response) => eventStream(response, view(1)));
  await withShell(await startHost(stream), stream, async (page, host) => {
    await page.goto(host.tenantUrl('stream.localhost', '/settings'));
    await page.locator('#sign-out').waitFor();
    await until(() => stream.requests.length === 1, 'The stream should open while signed in.');

    await page.locator('#sign-out').click();
    await page.locator('#sign-in-title').waitFor();
    await until(() => stream.open.size === 0, 'Sign-out should cancel the reader.');
    await pause(1500);

    assert.equal(stream.requests.length, 1);
  });
});

test('dropped stream reconnects and receives the current snapshot without a reload', { skip: !runsPublishedClient }, async () => {
  const stream = new StreamServer((record, response, count) => {
    if (count === 1) {
      eventStream(response, view(1));
      setTimeout(() => response.destroy(), 100);
      return;
    }
    eventStream(response, view(2));
  });
  await withShell(await startHost(stream), stream, async (page, host) => {
    let loads = 0;
    page.on('load', () => loads++);
    await page.goto(host.tenantUrl('stream.localhost'));
    await page.locator('#home-title').waitFor();
    await until(() => stream.requests.length === 2, 'The client should reconnect after the drop.', 15000);
    await until(() => stream.open.size === 1, 'The reconnected stream should stay open.');

    assert.equal(stream.requests[1].url, '/api/stream');
    assert.equal(loads, 1);
  });
});

test('stream 401 signs the client out without retrying', { skip: !runsPublishedClient }, async () => {
  const stream = new StreamServer((record, response) => response.writeHead(401).end());
  await withShell(await startHost(stream), stream, async (page, host) => {
    await page.goto(host.tenantUrl('stream.localhost'));
    await page.locator('#sign-in-title').waitFor();
    await pause(1500);

    assert.equal(stream.requests.length, 1);
  });
});

test('stream 403 stops the reader without retrying', { skip: !runsPublishedClient }, async () => {
  const stream = new StreamServer((record, response) => response.writeHead(403).end());
  await withShell(await startHost(stream), stream, async (page, host) => {
    await page.goto(host.tenantUrl('stream.localhost'));
    await page.locator('#home-title').waitFor();
    await pause(1500);

    assert.equal(stream.requests.length, 1);
    assert.equal(await page.locator('#home-title').count(), 1);
  });
});

test('stream client_outdated reloads once for the build and is not retried', { skip: !runsPublishedClient }, async () => {
  const stream = new StreamServer((record, response) => response.writeHead(426, { 'x-client-status': 'client_outdated' }).end());
  await withShell(await startHost(stream), stream, async (page, host) => {
    let loads = 0;
    page.on('load', () => loads++);
    await page.goto(host.tenantUrl('stream.localhost'));
    await until(() => loads >= 2, 'The outdated build should reload the page once.');
    await pause(2000);

    assert.equal(loads, 2);
    assert.equal(stream.requests.length, 2, 'One request per page load, none retried.');
  });
});

test('non-SSE success response is rejected and retried with a bounded delay', { skip: !runsPublishedClient }, async () => {
  const stream = new StreamServer((record, response, count) => {
    if (count <= 2) {
      response.writeHead(200, { 'content-type': 'application/json' }).end('event: closed\ndata: {}\n\n');
      return;
    }
    eventStream(response, view(1));
  });
  await withShell(await startHost(stream), stream, async (page, host) => {
    await page.goto(host.tenantUrl('stream.localhost'));
    await page.locator('#home-title').waitFor();
    await until(() => stream.requests.length === 3, 'The client should retry after a non-SSE response.', 15000);
    await until(() => stream.open.size === 1, 'The valid stream should stay open.');

    const [first, second, third] = stream.requests;
    assert.ok(second.at - first.at >= 200, 'Retries should be delayed, not immediate.');
    assert.ok(third.at - second.at < 5000, 'The delay should be bounded.');
    assert.equal(stream.requests.length, 3);
  });
});
