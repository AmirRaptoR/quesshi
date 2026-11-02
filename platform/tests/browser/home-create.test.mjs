import assert from 'node:assert/strict';
import { after, before, test } from 'node:test';
import { chromium } from 'playwright';
import { capturePageErrors, startPublishedPlatformHost } from './src/browser-harness.mjs';
import { startRealServer } from './src/real-server.mjs';

const publishedDirectory = process.env.PLATFORM_BROWSER_HOST_DIRECTORY;
const hostDll = process.env.PLATFORM_BROWSER_REAL_SERVER_DLL;
const runsAgainstRealServer = publishedDirectory !== undefined && hostDll !== undefined;

let browser;

before(async () => {
  browser = await chromium.launch({ headless: true });
});

after(async () => {
  await browser.close();
});

// The id of every lobby the signed-in player sits in, read from the real server's API rather than from the client.
async function serverLobbies(context, url) {
  const response = await context.request.get(new URL('/api/me/inbox', url).href);
  assert.equal(response.status(), 200);
  return (await response.json()).lobbies.map(lobby => lobby.id);
}

test('creating an async_duel session from Home opens the session the real server created', { skip: !runsAgainstRealServer, timeout: 240000 }, async () => {
  let server;
  let host;
  let context;
  try {
    server = await startRealServer({ hostDll });
    host = await startPublishedPlatformHost({
      directory: publishedDirectory,
      tenants: { [server.tenantHost]: { upstream: { host: server.tenantHost, port: server.port } } }
    });
    context = await browser.newContext();
    const page = await context.newPage();
    page.setDefaultTimeout(30000);
    const assertNoPageErrors = capturePageErrors(page);
    const url = host.tenantUrl(server.tenantHost);
    await page.goto(url);
    await page.locator('#email').fill(server.email);
    await page.locator('#email').press('Enter');
    await page.locator('#code').fill(await server.emailCode(server.email));
    await page.locator('#code').press('Enter');
    await page.locator('#home-title').waitFor();

    const create = page.locator('[data-test="home-new"] [data-format="async_duel"]');
    await create.waitFor();
    assert.deepEqual(await serverLobbies(context, url), []);

    await create.click();
    await page.waitForURL(/\/s\/[0-9a-f-]{36}$/);

    const created = await serverLobbies(context, url);
    assert.equal(created.length, 1, 'the server should hold exactly one session for the player');
    assert.equal(new URL(page.url()).pathname, `/s/${created[0]}`);
    assertNoPageErrors();
  } finally {
    await context?.close();
    await host?.close();
    await server?.close();
  }
});
