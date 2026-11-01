import assert from 'node:assert/strict';
import { after, before, test } from 'node:test';
import { chromium } from 'playwright';
import { capturePageErrors, startPublishedPlatformHost } from './src/browser-harness.mjs';

const publishedDirectory = process.env.PLATFORM_BROWSER_HOST_DIRECTORY;
const runsPublishedClient = publishedDirectory !== undefined;
const sessionId = '3f0f6d3c-7c4b-4a0a-9a52-1d1f6a3b0101';
const playerId = '21ac7193-52fd-4d4b-9194-5cd547448072';

// The session view the server streams after a moderator ends a session: status cancelled, no phase or result.
const cancelledView = {
  id: sessionId,
  code: 'ABCD23',
  format: 'live',
  status: 'cancelled',
  phase: 'lobby',
  slot: -1,
  step: 0,
  version: 5,
  configVersion: 1,
  now: '2026-01-01T00:00:00Z',
  deadline: null,
  me: { id: playerId, seat: 0, role: 'player', status: 'active', score: 0, place: null, outcome: 'none' },
  roster: { total: 1, seats: [{ id: playerId, name: 'Ada', seat: 0, role: 'player', status: 'active', score: 0, place: null, outcome: 'none', online: true }] },
  content: {},
  rules: {},
  body: {}
};

function tenant() {
  return {
    config: {
      name: 'Cancelled',
      languages: [{ code: 'en', displayName: 'English', direction: 'ltr' }],
      defaultLanguage: 'en',
      capabilities: {},
      configVersion: 1,
      serverBuildId: 'test'
    },
    theme: ':root { --shab: #114477; }',
    handlers: {
      '/api/stream': (request, response) => {
        response.writeHead(200, { 'content-type': 'text/event-stream', 'cache-control': 'no-cache' });
        response.write(': open\n\n');
        response.write(`event: view\ndata: ${JSON.stringify(cancelledView)}\n\n`);
      }
    },
    resources: {
      '/api/me': { body: `{"id":"${playerId}","displayName":"Ada","avatarSeed":"avatar-7","lang":"en"}`, type: 'application/json', tag: 'player' },
      '/api/antiforgery': { body: '{"token":"csrf"}', type: 'application/json', tag: 'csrf' }
    }
  };
}

let browser;

before(async () => {
  browser = await chromium.launch({ headless: true });
});

after(async () => {
  await browser.close();
});

test('a player viewing an admin-ended session sees the generic cancelled screen', { skip: !runsPublishedClient }, async () => {
  const host = await startPublishedPlatformHost({ directory: publishedDirectory, tenants: { 'cancelled.localhost': tenant() } });
  const context = await browser.newContext();
  const page = await context.newPage();
  const assertNoPageErrors = capturePageErrors(page);
  try {
    await page.goto(host.tenantUrl('cancelled.localhost', `/s/${sessionId}`));
    const screen = page.locator('[data-session-cancelled]');
    await screen.waitFor();
    assert.match(await screen.innerText(), /cancelled/i);
    assert.equal(await page.locator('[data-lobby-panel]').count(), 0);
    assertNoPageErrors();
  } finally {
    await context.close();
    await host.close();
  }
});
