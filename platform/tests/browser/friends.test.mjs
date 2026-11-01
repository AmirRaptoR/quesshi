import assert from 'node:assert/strict';
import { after, before, test } from 'node:test';
import { chromium } from 'playwright';
import { capturePageErrors, startPublishedPlatformHost } from './src/browser-harness.mjs';

const publishedDirectory = process.env.PLATFORM_BROWSER_HOST_DIRECTORY;
const runsPublishedClient = publishedDirectory !== undefined;
const ada = '3f0f6d3c-7c4b-4a0a-9a52-1d1f6a3b0001';
const bo = '3f0f6d3c-7c4b-4a0a-9a52-1d1f6a3b0002';
const cy = '3f0f6d3c-7c4b-4a0a-9a52-1d1f6a3b0003';

const json = (response, body, status = 200) => {
  response.writeHead(status, { 'content-type': 'application/json' }).end(JSON.stringify(body));
};

// Stateful friends API: the published client reads and mutates the same set the list route serves.
function friendsTenant({ enabled }) {
  const people = {
    [ada]: { id: ada, displayName: 'Ada', avatarSeed: 'seed-ada', isOnline: true },
    [bo]: { id: bo, displayName: 'Bo', avatarSeed: 'seed-bo', isOnline: false },
    [cy]: { id: cy, displayName: 'Cy', avatarSeed: 'seed-cy', isOnline: false }
  };
  const friends = new Set([ada, bo]);
  const calls = [];
  const gated = handle => (request, response) => {
    if (!enabled) return json(response, {}, 404);
    return handle(request, response);
  };
  const mutate = id => gated((request, response) => {
    calls.push({ method: request.method, id, csrf: request.headers['x-csrf'] });
    if (request.method === 'POST') friends.add(id);
    if (request.method === 'DELETE') friends.delete(id);
    response.writeHead(200).end();
  });
  return {
    calls,
    friends,
    config: {
      name: 'Friends',
      languages: [{ code: 'en', displayName: 'English', direction: 'ltr' }],
      defaultLanguage: 'en',
      capabilities: { format: [], source: ['bank'], feature: enabled ? ['friends'] : [] },
      configVersion: 1,
      serverBuildId: 'test'
    },
    theme: ':root { --shab: #114477; }',
    handlers: {
      '/api/stream': (request, response) => {
        response.writeHead(200, { 'content-type': 'text/event-stream', 'cache-control': 'no-cache' });
        response.write(': open\n\n');
      },
      '/api/me/sessions': (request, response) => json(response, { sessions: [], next: null }),
      '/api/x/friends': gated((request, response) =>
        json(response, [...friends].map(id => people[id]).sort((a, b) => a.displayName.localeCompare(b.displayName)))),
      '/api/x/friends/search': gated((request, response) => {
        const q = new URL(request.url, 'http://x').searchParams.get('q').toLowerCase();
        json(response, Object.values(people)
          .filter(p => !friends.has(p.id) && p.displayName.toLowerCase().includes(q))
          .map(({ id, displayName, avatarSeed }) => ({ id, displayName, avatarSeed })));
      }),
      ...Object.fromEntries([ada, bo, cy].map(id => [`/api/x/friends/${id}`, mutate(id)]))
    },
    resources: {
      '/api/me': { body: '{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","displayName":"Me","avatarSeed":"avatar-7","lang":"en"}', type: 'application/json', tag: 'player' },
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

async function withProfile(enabled, body) {
  const tenant = friendsTenant({ enabled });
  const host = await startPublishedPlatformHost({ directory: publishedDirectory, tenants: { 'friends.localhost': tenant } });
  const context = await browser.newContext();
  const page = await context.newPage();
  const assertNoPageErrors = capturePageErrors(page);
  try {
    await page.goto(`${host.tenantUrl('friends.localhost')}profile`);
    await page.locator('#profile-title').waitFor();
    await body(page, tenant);
    assertNoPageErrors();
  } finally {
    await context.close();
    await host.close();
  }
}

test('the profile shows friend names, avatars and online marks', { skip: !runsPublishedClient }, async () => {
  await withProfile(true, async page => {
    await page.locator('[data-test="friend"]').first().waitFor();
    assert.deepEqual(await page.locator('[data-test="friend-name"]').allTextContents(), ['Ada', 'Bo']);
    assert.deepEqual(await page.locator('[data-test="friend-avatar"]').evaluateAll(nodes => nodes.map(n => n.dataset.seed)), ['seed-ada', 'seed-bo']);
    assert.equal(await page.locator('[data-test="friend"]').nth(0).locator('[data-test="friend-online"]').count(), 1);
    assert.equal(await page.locator('[data-test="friend"]').nth(1).locator('[data-test="friend-online"]').count(), 0);
  });
});

test('adding a search result makes the friends list contain it', { skip: !runsPublishedClient }, async () => {
  await withProfile(true, async (page, tenant) => {
    await page.locator('[data-test="friend"]').first().waitFor();
    await page.locator('[data-test="friends-search"]').fill('cy');
    await page.locator('[data-test="friends-search-submit"]').click();
    await page.locator('[data-test="friends-add"]').click();
    await page.locator('[data-test="friend"]').nth(2).waitFor();
    assert.ok(tenant.friends.has(cy));
    assert.deepEqual(await page.locator('[data-test="friend-name"]').allTextContents(), ['Ada', 'Bo', 'Cy']);
    assert.deepEqual(tenant.calls, [{ method: 'POST', id: cy, csrf: 'csrf' }]);
  });
});

test('removing a friend makes the friends list omit it', { skip: !runsPublishedClient }, async () => {
  await withProfile(true, async (page, tenant) => {
    await page.locator('[data-test="friend"]').first().waitFor();
    await page.locator('[data-test="friend-remove"]').first().click();
    await page.locator('[data-test="friend"]').nth(1).waitFor({ state: 'detached' });
    assert.ok(!tenant.friends.has(ada));
    assert.deepEqual(await page.locator('[data-test="friend-name"]').allTextContents(), ['Bo']);
  });
});

test('the profile has no friends section when the tenant disables friends', { skip: !runsPublishedClient }, async () => {
  await withProfile(false, async page => {
    await page.waitForLoadState('networkidle');
    assert.equal(await page.locator('[data-test="friends-section"]').count(), 0);
  });
});
