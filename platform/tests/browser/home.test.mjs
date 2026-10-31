import assert from 'node:assert/strict';
import { after, before, test } from 'node:test';
import { chromium } from 'playwright';
import { capturePageErrors, startPublishedPlatformHost } from './src/browser-harness.mjs';

const publishedDirectory = process.env.PLATFORM_BROWSER_HOST_DIRECTORY;
const runsPublishedClient = publishedDirectory !== undefined;
const invited = '3f0f6d3c-7c4b-4a0a-9a52-1d1f6a3b0002';
const lobby = '3f0f6d3c-7c4b-4a0a-9a52-1d1f6a3b0003';

const json = (response, body, status = 200) => {
  response.writeHead(status, { 'content-type': 'application/json' }).end(JSON.stringify(body));
};

async function readBody(request) {
  let text = '';
  for await (const chunk of request) text += chunk;
  return text ? JSON.parse(text) : null;
}

// Minimal stateful tenant: the pages under test call these routes and the stub answers the way the server does.
function homeTenant() {
  const calls = [];
  const state = { seated: false, invitations: [{ sessionId: invited, invitedBy: '3f0f6d3c-7c4b-4a0a-9a52-1d1f6a3b0009', expiresAt: '2030-01-01T00:00:00Z' }] };
  const tenant = {
    calls,
    config: {
      name: 'Home',
      languages: [{ code: 'en', displayName: 'English', direction: 'ltr' }],
      defaultLanguage: 'en',
      capabilities: { format: [], source: ['bank'] },
      configVersion: 1,
      serverBuildId: 'test'
    },
    theme: ':root { --shab: #114477; }',
    handlers: {
      '/api/stream': (request, response) => {
        response.writeHead(200, { 'content-type': 'text/event-stream', 'cache-control': 'no-cache' });
        response.write(': open\n\n');
      },
      '/api/me/inbox': (request, response) => json(response, {
        version: 1,
        invitations: state.invitations,
        lobbies: state.seated ? [{ id: lobby, code: 'ABCD', format: 'async_duel' }] : [],
        turns: [],
        items: []
      }),
      '/api/sessions': async (request, response) => {
        const body = await readBody(request);
        calls.push({ path: '/api/sessions', body });
        json(response, { id: body.id, code: 'ABCD' }, 201);
      },
      '/api/sessions/join': async (request, response) => {
        const body = await readBody(request);
        calls.push({ path: '/api/sessions/join', body });
        if (body.code !== 'ABCD') return json(response, { error: 'not_found' }, 404);
        state.seated = true;
        json(response, { code: 'joined', version: 2 });
      },
      [`/api/sessions/${invited}/invites/accept`]: (request, response) => {
        calls.push({ path: 'accept' });
        state.invitations = [];
        json(response, { code: 'joined', version: 2 });
      },
      [`/api/sessions/${invited}/invites/decline`]: (request, response) => {
        calls.push({ path: 'decline' });
        state.invitations = [];
        json(response, { code: 'declined' });
      }
    },
    resources: {
      '/api/me': { body: '{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","displayName":"Ada","avatarSeed":"avatar-7","lang":"en"}', type: 'application/json', tag: 'player' },
      '/api/antiforgery': { body: '{"token":"csrf"}', type: 'application/json', tag: 'csrf' }
    }
  };
  return tenant;
}

let browser;

before(async () => {
  browser = await chromium.launch({ headless: true });
});

after(async () => {
  await browser.close();
});

async function withHome(body) {
  const tenant = homeTenant();
  const host = await startPublishedPlatformHost({ directory: publishedDirectory, tenants: { 'home.localhost': tenant } });
  const context = await browser.newContext();
  const page = await context.newPage();
  const assertNoPageErrors = capturePageErrors(page);
  try {
    await page.goto(host.tenantUrl('home.localhost'));
    await page.locator('#home-title').waitFor();
    await body(page, tenant);
    assertNoPageErrors();
  } finally {
    await context.close();
    await host.close();
  }
}

test('joining by a valid code seats the player and opens the session', { skip: !runsPublishedClient }, async () => {
  await withHome(async (page, tenant) => {
    await page.locator('input[name="code"]').fill('ABCD');
    await page.locator('[data-test="join-submit"]').click();
    await page.waitForURL(`**/s/${lobby}`);
    assert.deepEqual(tenant.calls.find(c => c.path === '/api/sessions/join').body, { code: 'ABCD' });
  });
});

test('accepting an invitation seats the recipient and opens the session', { skip: !runsPublishedClient }, async () => {
  await withHome(async (page, tenant) => {
    await page.locator(`[data-invitation="${invited}"] [data-action="accept"]`).click();
    await page.waitForURL(`**/s/${invited}`);
    assert.ok(tenant.calls.some(c => c.path === 'accept'));
  });
});

test('declining an invitation removes its card without seating the player', { skip: !runsPublishedClient }, async () => {
  await withHome(async (page, tenant) => {
    await page.locator(`[data-invitation="${invited}"] [data-action="decline"]`).click();
    await page.locator(`[data-invitation="${invited}"]`).waitFor({ state: 'detached' });
    assert.ok(tenant.calls.some(c => c.path === 'decline'));
    assert.ok(!tenant.calls.some(c => c.path === 'accept'));
    assert.equal(new URL(page.url()).pathname, '/');
  });
});
