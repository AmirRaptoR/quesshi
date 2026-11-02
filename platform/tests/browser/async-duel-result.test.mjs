import assert from 'node:assert/strict';
import { after, before, test } from 'node:test';
import { chromium } from 'playwright';
import { capturePageErrors, startPublishedPlatformHost } from './src/browser-harness.mjs';

const publishedDirectory = process.env.PLATFORM_BROWSER_HOST_DIRECTORY;
const runsPublishedClient = publishedDirectory !== undefined;
const sessionId = '3f0f6d3c-7c4b-4a0a-9a52-1d1f6a3b0455';
const total = 10;
const players = {
  ada: { id: '21ac7193-52fd-4d4b-9194-5cd547448072', name: 'Ada', seat: 0, pick: 0 },
  bob: { id: '21ac7193-52fd-4d4b-9194-5cd547448073', name: 'Bob', seat: 1, pick: 1 }
};

const json = (response, body, status = 200) => response.writeHead(status, { 'content-type': 'application/json' }).end(JSON.stringify(body));

async function readBody(request) {
  let text = '';
  for await (const chunk of request) text += chunk;
  return text ? JSON.parse(text) : null;
}

// One duel shared by both players' tenants: each player has a personal run, and the session ends when both are done.
function duel() {
  const state = { version: 1, finished: false, runs: { ada: run(), bob: run() }, streams: { ada: new Set(), bob: new Set() } };

  function run() {
    return { phase: 'idle', slot: -1, answers: [], done: false };
  }

  const score = key => state.runs[key].answers.reduce((sum, answer) => sum + answer.points, 0);
  const placeOf = key => (!state.finished ? null : score(key) >= score(key === 'ada' ? 'bob' : 'ada') ? 1 : 2);
  const outcomeOf = key => (!state.finished ? 'none' : placeOf(key) === 1 ? 'win' : 'loss');

  function seat(key) {
    const p = players[key];
    const r = state.runs[key];
    return { id: p.id, name: p.name, seat: p.seat, role: 'player', status: r.done ? 'done' : 'active', score: score(key), place: placeOf(key), outcome: outcomeOf(key), online: true };
  }

  function runView(key, includePrivate) {
    const r = state.runs[key];
    const served = includePrivate && r.slot >= 0 && !r.done;
    return {
      player: players[key].id,
      kind: served ? 'choice' : null,
      kindVersion: served ? 1 : null,
      card: served ? { options: ['Tehran', 'Paris'] } : null,
      reveal: served && r.phase === '1' ? { correctIndex: 0, correctOption: 'Tehran' } : null,
      answers: r.answers.map(answer => ({ stored: { index: answer.index }, isGraded: true, credit: answer.credit, points: answer.points, miss: false })),
      total: served ? total : null
    };
  }

  function view(key) {
    const other = key === 'ada' ? 'bob' : 'ada';
    const r = state.runs[key];
    const mayShowOthers = state.finished || r.done;
    return {
      id: sessionId,
      code: 'ABCD23',
      format: 'async_duel',
      status: state.finished ? 'finished' : 'running',
      phase: state.finished ? '2' : r.phase,
      slot: Math.max(r.slot, 0),
      step: 0,
      version: state.version,
      configVersion: 1,
      now: new Date().toISOString(),
      deadline: r.phase === '0' ? new Date(Date.now() + 20000).toISOString() : null,
      me: { id: players[key].id, seat: players[key].seat, role: 'player', status: r.done ? 'done' : 'active', score: score(key), place: placeOf(key), outcome: outcomeOf(key) },
      roster: { total: 2, seats: [seat('ada'), seat('bob')] },
      content: { count: total },
      rules: {},
      body: { you: runView(key, true), others: mayShowOthers ? [runView(other, false)] : [] }
    };
  }

  function apply(key, command) {
    const r = state.runs[key];
    if (command.type === 'next' && r.phase === 'idle') Object.assign(r, { phase: '0', slot: 0 });
    else if (command.type === 'answer' && r.phase === '0') {
      const index = command.payload.index;
      r.answers.push({ index, credit: index === 0 ? 1 : 0, points: index === 0 ? 80 : 0 });
      r.phase = '1';
    } else if (command.type === 'next' && r.phase === '1') {
      if (r.slot + 1 < total) Object.assign(r, { phase: '0', slot: r.slot + 1 });
      else Object.assign(r, { phase: '2', done: true });
    } else return;
    state.finished = state.runs.ada.done && state.runs.bob.done;
    state.version += 1;
    for (const streams of Object.values(state.streams)) for (const response of streams) response.write(`event: view\ndata: ${JSON.stringify(view(streams === state.streams.ada ? 'ada' : 'bob'))}\n\n`);
  }

  return { state, view, apply };
}

function tenant(key, shared) {
  const p = players[key];
  return {
    config: {
      name: p.name,
      languages: [{ code: 'en', displayName: 'English', direction: 'ltr' }],
      defaultLanguage: 'en',
      capabilities: { format: ['async_duel'], source: ['bank'] },
      configVersion: 1,
      serverBuildId: 'test'
    },
    theme: ':root { --shab: #114477; }',
    handlers: {
      '/api/stream': (request, response) => {
        response.writeHead(200, { 'content-type': 'text/event-stream', 'cache-control': 'no-cache' });
        response.write(': open\n\n');
        response.write(`event: view\ndata: ${JSON.stringify(shared.view(key))}\n\n`);
        shared.state.streams[key].add(response);
        response.on('close', () => shared.state.streams[key].delete(response));
      },
      [`/api/sessions/${sessionId}/commands`]: async (request, response) => {
        shared.apply(key, await readBody(request));
        json(response, shared.view(key));
      },
      [`/api/sessions/${sessionId}/history`]: (request, response) => {
        const query = new URL(request.url, 'http://localhost').searchParams;
        const after = Number(query.get('after') ?? -1);
        const limit = Number(query.get('limit') ?? 20);
        const slots = Array.from({ length: total }, (_, slot) => slot).filter(slot => slot > after).slice(0, limit);
        const answers = shared.state.runs[key].answers;
        json(response, {
          items: slots.map(slot => ({
            slot,
            kind: 'choice',
            kindVersion: 1,
            prompt: `Question ${slot}`,
            card: { options: ['Tehran', 'Paris'] },
            reveal: { correctIndex: 0, correctOption: 'Tehran' },
            answers: answers[slot] ? [{ index: answers[slot].index }] : []
          })),
          next: slots.length > 0 && slots.at(-1) < total - 1 ? slots.at(-1) : null
        });
      }
    },
    resources: {
      '/api/me': { body: JSON.stringify({ id: p.id, displayName: p.name, avatarSeed: `avatar-${p.seat}`, lang: 'en' }), type: 'application/json', tag: `player-${key}` },
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

async function playToTheEnd(page) {
  await page.locator('[data-duel-start]').click();
  for (let slot = 0; slot < total; slot += 1) {
    await page.locator(`[data-duel-slot]:has-text("${slot + 1} / ${total}")`).waitFor();
    await page.locator('[data-duel-question] [data-choice]').first().waitFor();
    await page.locator(`[data-duel-question] [data-choice='${page.pick}']`).click();
    await page.locator('[data-duel-reveal]').waitFor();
    await page.locator('[data-duel-next]').click();
  }
}

async function standings(page) {
  await page.locator('[data-duel-result]').waitFor();
  await page.locator('[data-standing]').first().waitFor();
  return page.locator('[data-standing]').evaluateAll(rows => rows.map(row => ({
    id: row.getAttribute('data-standing'),
    place: row.querySelector('[data-standing-place]').textContent.trim(),
    score: row.querySelector('.stand-row__score').textContent.trim()
  })));
}

test('two players play a ten-question duel at different times and both reach the same standings', { skip: !runsPublishedClient }, async () => {
  const shared = duel();
  const host = await startPublishedPlatformHost({
    directory: publishedDirectory,
    tenants: { 'ada.localhost': tenant('ada', shared), 'bob.localhost': tenant('bob', shared) }
  });
  const adaContext = await browser.newContext();
  const bobContext = await browser.newContext();
  const ada = await adaContext.newPage();
  const bob = await bobContext.newPage();
  ada.pick = players.ada.pick;
  bob.pick = players.bob.pick;
  const assertAdaClean = capturePageErrors(ada);
  const assertBobClean = capturePageErrors(bob);
  try {
    await ada.goto(host.tenantUrl('ada.localhost', `/s/${sessionId}`));
    await playToTheEnd(ada);
    await ada.locator('[data-duel-waiting]').waitFor();

    // Bob arrives later, after Ada has finished and is waiting.
    await bob.goto(host.tenantUrl('bob.localhost', `/s/${sessionId}`));
    await playToTheEnd(bob);

    const adaStandings = await standings(ada);
    const bobStandings = await standings(bob);
    assert.deepEqual(adaStandings, bobStandings);
    assert.deepEqual(adaStandings.map(row => row.id), [players.ada.id, players.bob.id]);
    assert.equal(await ada.locator('[data-duel-question-result]').count() > 0, true);
    assertAdaClean();
    assertBobClean();
  } finally {
    await adaContext.close();
    await bobContext.close();
    await host.close();
  }
});

test('a running session in a format the tenant has since turned off stays playable', { skip: !runsPublishedClient }, async () => {
  const shared = duel();
  const disabled = tenant('ada', shared);
  disabled.config.capabilities = { format: [], source: ['bank'] };
  const host = await startPublishedPlatformHost({ directory: publishedDirectory, tenants: { 'ada.localhost': disabled } });
  const context = await browser.newContext();
  const page = await context.newPage();
  page.pick = players.ada.pick;
  const assertClean = capturePageErrors(page);
  try {
    await page.goto(host.tenantUrl('ada.localhost', `/s/${sessionId}`));
    await page.locator('[data-duel-start]').click();
    await page.locator(`[data-duel-slot]:has-text("1 / ${total}")`).waitFor();
    await page.locator(`[data-duel-question] [data-choice='${page.pick}']`).click();
    await page.locator('[data-duel-reveal]').waitFor();
    assertClean();
  } finally {
    await context.close();
    await host.close();
  }
});
