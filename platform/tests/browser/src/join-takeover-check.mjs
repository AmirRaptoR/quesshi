// Drives the invite page's hand-over to the WebAssembly app in a real browser against the real server.
// Run by the .NET JoinTakeoverTests with one scenario name; it is not a node:test file.
//
// The server under test does not serve the published client, so a small proxy stands in for the web
// front: it serves the published client files itself and forwards the join page, the API and the rest
// to the real server (with its tenant host and the player's session). It counts the join calls.
// Prints one JSON line: { joinRequests }.
import assert from 'node:assert/strict';
import { createServer, request as httpRequest } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { extname, normalize, resolve, sep } from 'node:path';
import { chromium } from 'playwright';

const scenario = process.argv[2];
const serverPort = Number(process.env.PLATFORM_JOIN_PORT);
const tenantHost = process.env.PLATFORM_JOIN_HOST;
const clientDirectory = resolve(process.env.PLATFORM_JOIN_CLIENT_DIR ?? '');
const code = process.env.PLATFORM_JOIN_CODE;
const playerCookie = process.env.PLATFORM_JOIN_PLAYER_COOKIE;
if (!scenario || !serverPort || !tenantHost || !process.env.PLATFORM_JOIN_CLIENT_DIR || !code) {
  throw new Error('A scenario and PLATFORM_JOIN_PORT, _HOST, _CLIENT_DIR and _CODE are required.');
}

const contentTypes = new Map([
  ['.css', 'text/css'], ['.html', 'text/html'], ['.js', 'text/javascript'], ['.json', 'application/json'],
  ['.wasm', 'application/wasm'], ['.png', 'image/png'], ['.svg', 'image/svg+xml'], ['.woff2', 'font/woff2']
]);

function startProxy({ signedIn, startDelayMs }) {
  const jar = new Map();
  if (signedIn) {
    const [name, ...value] = playerCookie.split('=');
    jar.set(name, value.join('='));
  }
  const held = { release: () => {} };
  const gate = new Promise(release => { held.release = release; });
  const state = { joinRequests: 0, release: held.release, held: false };

  async function serveClient(pathname, response) {
    const path = resolve(clientDirectory, normalize(decodeURIComponent(pathname)).replace(/^[/\\]+/, ''));
    if (!path.startsWith(`${clientDirectory}${sep}`) || !(await stat(path).catch(() => null))?.isFile()) return false;
    if (pathname === '/_framework/blazor.webassembly.js') {
      if (startDelayMs === 'hold') await gate;
      else if (startDelayMs > 0) await new Promise(done => setTimeout(done, startDelayMs));
    }
    response.writeHead(200, { 'content-type': contentTypes.get(extname(path)) ?? 'application/octet-stream', 'cache-control': 'no-store' });
    response.end(await readFile(path));
    return true;
  }

  function forward(request, response, body) {
    const headers = { ...request.headers, host: tenantHost, origin: `http://${tenantHost}` };
    delete headers['accept-encoding'];
    headers.cookie = [...jar].map(([name, value]) => `${name}=${value}`).join('; ');
    const upstream = httpRequest({ host: '127.0.0.1', port: serverPort, method: request.method, path: request.url, headers }, reply => {
      for (const cookie of reply.headers['set-cookie'] ?? []) {
        const [pair] = cookie.split(';');
        const at = pair.indexOf('=');
        if (/max-age=0|expires=thu, 01 jan 1970/i.test(cookie)) jar.delete(pair.slice(0, at));
        else jar.set(pair.slice(0, at), pair.slice(at + 1));
      }
      const replyHeaders = { ...reply.headers };
      delete replyHeaders['set-cookie'];
      response.writeHead(reply.statusCode, replyHeaders);
      reply.pipe(response);
    });
    upstream.on('error', () => response.destroy());
    response.on('close', () => upstream.destroy());
    upstream.end(body);
  }

  const server = createServer(async (request, response) => {
    const pathname = new URL(request.url, 'http://x').pathname;
    const chunks = [];
    for await (const chunk of request) chunks.push(chunk);
    if (request.method === 'POST' && pathname === '/api/sessions/join') state.joinRequests++;
    const serverOwned = pathname === '/' || pathname.startsWith('/join/') || pathname.startsWith('/api/') || pathname === '/theme.css' || pathname === '/manifest.webmanifest';
    if (!serverOwned && request.method === 'GET' && await serveClient(pathname, response)) return;
    forward(request, response, Buffer.concat(chunks));
  });
  return new Promise(done => server.listen(0, '127.0.0.1', () => done({
    state,
    url: `http://127.0.0.1:${server.address().port}`,
    close: () => { server.closeAllConnections(); server.close(); }
  })));
}

const scenarios = {
  // AC-1, AC-2: join pressed while the app start is held back sends nothing; once the app starts, exactly one call.
  async held({ page, proxy, url }) {
    // The held script blocks the load event, so the page is used as soon as it has arrived.
    await page.goto(`${url}/join/${code}`, { waitUntil: 'commit' });
    await page.locator('#join-button').click();
    await page.locator('#join-spinner').waitFor();
    assert.equal(await page.locator('#join-button').getAttribute('aria-busy'), 'true');
    await page.waitForTimeout(700);
    assert.equal(proxy.state.joinRequests, 0, 'nothing may be sent before the app is ready');
    assert.ok(await page.locator('#preview').isVisible(), 'the preview stays until the app is ready');
    proxy.state.release();
    await page.waitForURL(/\/s\/[0-9a-f-]{36}$/);
    await page.waitForTimeout(500);
  },
  // AC-3: with no press, the ready app removes the preview and shows itself in #app.
  async ready({ page, proxy, url }) {
    await page.goto(`${url}/join/${code}`);
    await page.locator('#app [data-test=join]').waitFor();
    assert.equal(await page.locator('#preview').count(), 0);
    assert.ok(await page.locator('#app').isVisible());
    assert.equal(await page.locator('#app').getAttribute('hidden'), null);
    await page.waitForTimeout(500);
    assert.equal(proxy.state.joinRequests, 0, 'the app joins only after join was pressed');
  },
  // AC-4: a signed-in player who pressed join lands in the lobby.
  async signedIn({ page, url }) {
    await page.goto(`${url}/join/${code}`, { waitUntil: 'commit' });
    await page.locator('#join-button').click();
    await page.waitForURL(/\/s\/[0-9a-f-]{36}$/);
    await page.waitForTimeout(500);
  },
  // AC-5: a new guest enters a name, then joins.
  async guest({ page, url }) {
    await page.goto(`${url}/join/${code}`, { waitUntil: 'commit' });
    await page.locator('#join-button').click();
    await page.locator('#guest-name').fill('Guest Gwen');
    await page.locator('.guest-entry button[type=submit]').click();
    await page.waitForURL(/\/s\/[0-9a-f-]{36}$/);
    await page.waitForTimeout(500);
  },
  // AC-6: an expired code shows the expired outcome and stays on the invite.
  async expired({ page, url }) {
    await page.goto(`${url}/join/${code}`, { waitUntil: 'commit' });
    await page.locator('#join-button').click();
    await page.locator('[data-test=join-error]').waitFor();
    assert.equal((await page.locator('[data-test=join-error]').innerText()).trim(), 'That invitation has expired.');
    assert.match(page.url(), new RegExp(`/join/${code}$`));
  },
  // AC-7: a start slowed by seconds still completes the takeover, with one call.
  async slow({ page, url }) {
    await page.goto(`${url}/join/${code}`, { waitUntil: 'commit' });
    await page.locator('#join-button').click();
    await page.waitForURL(/\/s\/[0-9a-f-]{36}$/, { timeout: 90_000 });
    await page.waitForTimeout(500);
  }
};

const settings = {
  held: { signedIn: true, startDelayMs: 'hold' },
  ready: { signedIn: true, startDelayMs: 0 },
  signedIn: { signedIn: true, startDelayMs: 3000 },
  guest: { signedIn: false, startDelayMs: 3000 },
  expired: { signedIn: true, startDelayMs: 3000 },
  slow: { signedIn: true, startDelayMs: 6000 }
};

if (!scenarios[scenario]) throw new Error(`Unknown scenario '${scenario}'.`);
const proxy = await startProxy(settings[scenario]);
const browser = await chromium.launch({ headless: true });
try {
  const context = await browser.newContext({ locale: 'en-US', serviceWorkers: 'block' });
  const page = await context.newPage();
  // A cold CI runner boots the WebAssembly client slowly, on top of the deliberate start delay.
  page.setDefaultTimeout(90_000);
  const errors = [];
  page.on('pageerror', error => errors.push(error));
  await scenarios[scenario]({ page, proxy, url: proxy.url });
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({ joinRequests: proxy.state.joinRequests }));
} finally {
  await browser.close();
  proxy.close();
}
