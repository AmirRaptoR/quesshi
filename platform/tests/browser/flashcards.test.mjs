import assert from 'node:assert/strict';
import { after, before, test } from 'node:test';
import { chromium } from 'playwright';
import { capturePageErrors, startPublishedPlatformHost } from './src/browser-harness.mjs';

const publishedDirectory = process.env.PLATFORM_BROWSER_HOST_DIRECTORY;
const runsPublishedClient = publishedDirectory !== undefined;
const sessionId = '3f0f6d3c-7c4b-4a0a-9a52-1d1f6a3b0466';
const player = { id: '21ac7193-52fd-4d4b-9194-5cd547448072', name: 'Ada' };
const total = 10;

const json = (response, body, status = 200) => response.writeHead(status, { 'content-type': 'application/json' }).end(JSON.stringify(body));

async function readBody(request) {
  let text = '';
  for await (const chunk of request) text += chunk;
  return text ? JSON.parse(text) : null;
}

// One solo deck: the back text of a card leaves the server only in the view served after that card was flipped.
function deck() {
  const state = { version: 1, slot: 0, side: 'front', counts: [0, 0, 0, 0], finished: false, streams: new Set() };
  const ratingOf = slot => slot % 4;

  function view() {
    const body = state.finished
      ? { counts: state.counts, total }
      : { slot: state.slot, phase: state.side === 'front' ? 0 : 1, side: state.side, card: { front: `front-${state.slot}` }, back: state.side === 'back' ? { back: `back-${state.slot}` } : null, total, rated: state.slot, kind: 'flashcard', kindVersion: 1 };
    return {
      id: sessionId,
      code: 'ABCD23',
      format: 'flashcards',
      status: state.finished ? 'finished' : 'running',
      phase: state.side === 'front' ? '0' : '1',
      slot: state.slot,
      step: 0,
      version: state.version,
      configVersion: 1,
      now: new Date().toISOString(),
      deadline: null,
      me: { id: player.id, seat: 0, role: 'player', status: state.finished ? 'done' : 'active', score: 0, place: null, outcome: null },
      roster: { total: 1, seats: [] },
      content: { source: 'pack', parameters: { packId: '5a1c9f20-0000-4000-8000-0000000000bb' }, count: total },
      rules: {},
      body
    };
  }

  function apply(command) {
    if (command.type === 'action' && command.name === 'flip' && state.side === 'front') state.side = 'back';
    else if (command.type === 'answer' && state.side === 'back') {
      state.counts[command.payload.rating] += 1;
      if (state.slot + 1 < total) Object.assign(state, { slot: state.slot + 1, side: 'front' });
      else state.finished = true;
    } else return;
    state.version += 1;
    for (const response of state.streams) response.write(`event: view\ndata: ${JSON.stringify(view())}\n\n`);
  }

  return { state, view, apply, ratingOf };
}

function tenant(shared, served) {
  const send = (response, body) => {
    const text = JSON.stringify(body);
    served.push(text);
    json(response, body);
  };
  return {
    config: {
      name: player.name,
      languages: [{ code: 'en', displayName: 'English', direction: 'ltr' }],
      defaultLanguage: 'en',
      capabilities: { format: ['flashcards'], source: ['pack'] },
      configVersion: 1,
      serverBuildId: 'test'
    },
    theme: ':root { --shab: #114477; }',
    handlers: {
      '/api/stream': (request, response) => {
        response.writeHead(200, { 'content-type': 'text/event-stream', 'cache-control': 'no-cache' });
        response.write(': open\n\n');
        const text = JSON.stringify(shared.view());
        served.push(text);
        response.write(`event: view\ndata: ${text}\n\n`);
        shared.state.streams.add(response);
        response.on('close', () => shared.state.streams.delete(response));
      },
      [`/api/sessions/${sessionId}`]: (request, response) => send(response, shared.view()),
      [`/api/sessions/${sessionId}/commands`]: async (request, response) => {
        const command = await readBody(request);
        shared.apply(command);
        send(response, shared.view());
      }
    },
    resources: {
      '/api/me': { body: JSON.stringify({ id: player.id, displayName: player.name, avatarSeed: 'avatar-0', lang: 'en' }), type: 'application/json', tag: 'player' },
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

async function open(host, page) {
  await page.goto(host.tenantUrl('ada.localhost', `/s/${sessionId}`));
  await page.locator('[data-flashcards-screen]').waitFor();
}

test('a ten-card deck is studied to the end and the back text is withheld until each flip', { skip: !runsPublishedClient }, async () => {
  const shared = deck();
  const served = [];
  const host = await startPublishedPlatformHost({ directory: publishedDirectory, tenants: { 'ada.localhost': tenant(shared, served) } });
  const context = await browser.newContext();
  const page = await context.newPage();
  const assertClean = capturePageErrors(page);
  try {
    await open(host, page);
    for (let slot = 0; slot < total; slot += 1) {
      await page.locator(`[data-flashcards-progress]:has-text("${slot + 1} of ${total}")`).waitFor();
      await page.locator('[data-flashcards-flip]').waitFor();
      assert.equal(await page.locator('[data-flashcard-back]').count(), 0);
      assert.equal((await page.content()).includes(`back-${slot}`), false);
      assert.equal(served.some(text => text.includes(`back-${slot}`)), false, `back-${slot} was served before the flip`);
      await page.locator('[data-flashcards-flip]').click();
      await page.locator('[data-flashcard-back]').waitFor();
      assert.equal(await page.locator('[data-flashcard-back]').textContent(), `back-${slot}`);
      await page.locator(`[data-flashcards-card] button[data-rating='${shared.ratingOf(slot)}']`).click();
    }
    await page.locator('[data-flashcards-result]').waitFor();
    const counts = await page.locator('[data-flashcards-counts] li').evaluateAll(rows => rows.map(row => Number(row.getAttribute('data-count'))));
    assert.deepEqual(counts, [3, 3, 2, 2]);
    assertClean();
  } finally {
    await context.close();
    await host.close();
  }
});

test('reloading mid-deck returns to the same card and side', { skip: !runsPublishedClient }, async () => {
  const shared = deck();
  const host = await startPublishedPlatformHost({ directory: publishedDirectory, tenants: { 'ada.localhost': tenant(shared, []) } });
  const context = await browser.newContext();
  const page = await context.newPage();
  const assertClean = capturePageErrors(page);
  try {
    await open(host, page);
    for (let slot = 0; slot < 4; slot += 1) {
      await page.locator('[data-flashcards-flip]').click();
      await page.locator(`[data-flashcards-card] button[data-rating='${shared.ratingOf(slot)}']`).click();
      await page.locator(`[data-flashcards-progress]:has-text("${slot + 2} of ${total}")`).waitFor();
    }

    await page.locator('[data-flashcards-flip]').click();
    await page.locator('[data-flashcard-back]').waitFor();
    await page.reload();
    await page.locator('[data-flashcards-screen]').waitFor();
    await page.locator('[data-flashcards-progress]:has-text("5 of 10")').waitFor();
    assert.equal(await page.locator('[data-flashcards-card]').getAttribute('data-side'), 'back');
    assert.equal(await page.locator('[data-flashcard-back]').textContent(), 'back-4');

    await page.locator("[data-flashcards-card] button[data-rating='0']").click();
    await page.locator('[data-flashcards-progress]:has-text("6 of 10")').waitFor();
    await page.reload();
    await page.locator('[data-flashcards-progress]:has-text("6 of 10")').waitFor();
    assert.equal(await page.locator('[data-flashcards-card]').getAttribute('data-side'), 'front');
    assertClean();
  } finally {
    await context.close();
    await host.close();
  }
});
