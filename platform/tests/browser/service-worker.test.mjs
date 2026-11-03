import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { test } from 'node:test';
import vm from 'node:vm';
import { loadWorker } from './src/worker-harness.mjs';

const published = process.env.PLATFORM_BROWSER_HOST_DIRECTORY;
const publishedManifest = published && existsSync(join(published, 'service-worker-assets.js'))
  ? (() => {
      const sandbox = { self: {} };
      vm.runInNewContext(readFileSync(join(published, 'service-worker-assets.js'), 'utf8'), sandbox);
      return sandbox.self.assetsManifest;
    })()
  : null;

const assets = (publishedManifest?.assets ?? [
  { url: 'index.html', hash: 'sha256-a' },
  { url: 'css/app.css', hash: 'sha256-b' },
  { url: '_framework/dotnet.native.abc.wasm', hash: 'sha256-c' },
  { url: '_framework/Quesshi.Web.abc.wasm', hash: 'sha256-d' },
  { url: '_framework/Quesshi.Web.Admin.abc.wasm', hash: 'sha256-e' },
  { url: '_framework/Quesshi.Kinds.MapPick.Client.abc.wasm', hash: 'sha256-f' },
  { url: '_content/Quesshi.Kinds.MapPick.Client/maps/world.svg', hash: 'sha256-g' },
  { url: '_content/Quesshi.Kinds.MapPick.Client/WorldMap.razor.js', hash: 'sha256-h' }
]);
const lazy = /Quesshi\.Web\.Admin|Quesshi\.Kinds\.MapPick\.Client/;

const ok = body => new Response(body, { status: 200 });

test('install fetches neither lazy assembly nor the world map', async () => {
  const worker = loadWorker({ assets, version: 'v1', network: () => ok('x') });
  await worker.install();
  assert.ok(worker.fetched.length > 0);
  assert.deepEqual(worker.fetched.filter(path => lazy.test(path)), []);
  assert.deepEqual(worker.fetched.filter(path => path.endsWith('world.svg')), []);
});

test('a lazy asset is fetched once on first use and then served from the worker cache', async () => {
  const path = '/_framework/Quesshi.Web.Admin.abc.wasm';
  let online = true;
  const worker = loadWorker({ assets, version: 'v1', network: () => { if (!online) throw new Error('offline'); return ok('admin'); } });
  await worker.install();
  assert.equal(await (await worker.request(path)).text(), 'admin');
  online = false;
  assert.equal(await (await worker.request(path)).text(), 'admin');
  assert.equal(worker.fetched.filter(p => p === path).length, 1);
});

for (const path of ['/theme.css', '/manifest.webmanifest', '/api/config']) {
  test(`${path} is revalidated online and the last good copy is served offline`, async () => {
    let online = true;
    const seen = [];
    const worker = loadWorker({
      assets, version: 'v1',
      network: request => {
        if (!online) throw new Error('offline');
        seen.push(request.headers.get('if-none-match'));
        return seen.length === 1
          ? new Response('first', { status: 200, headers: { etag: '"1"' } })
          : new Response(null, { status: 304 });
      }
    });
    assert.equal(await (await worker.request(path)).text(), 'first');
    assert.equal(await (await worker.request(path)).text(), 'first');
    assert.deepEqual(seen, [null, '"1"']);
    online = false;
    assert.equal(await (await worker.request(path)).text(), 'first');
  });
}

test('a tab on the previous build keeps its fingerprinted files after the worker updates', async () => {
  const oldFile = '_framework/Quesshi.Web.old.wasm';
  const v1 = loadWorker({ assets: [{ url: oldFile, hash: 'sha256-o' }, ...assets.slice(0, 2)], version: 'v1', network: () => ok('old build') });
  await v1.install();
  await v1.request('/_framework/Quesshi.Web.Admin.old.wasm');
  const v2 = loadWorker({ assets: assets.slice(0, 2), version: 'v2', caches: v1.caches, network: () => { throw new Error('offline'); } });
  await v2.install();
  await v2.activate();
  assert.equal(await (await v2.request(`/${oldFile}`)).text(), 'old build');
  assert.equal(await (await v2.request('/_framework/Quesshi.Web.Admin.old.wasm')).text(), 'old build');
});

test('an unfingerprinted lazy file is refetched after a release, with the old copy only as the offline fallback', async () => {
  const path = '/_content/Quesshi.Kinds.MapPick.Client/maps/world.svg';
  const v1 = loadWorker({ assets: assets.slice(0, 2), version: 'v1', network: () => ok('map one') });
  await v1.install();
  assert.equal(await (await v1.request(path)).text(), 'map one');
  let online = true;
  const v2 = loadWorker({ assets: assets.slice(0, 2), version: 'v2', caches: v1.caches, network: () => { if (!online) throw new Error('offline'); return ok('map two'); } });
  await v2.install();
  await v2.activate();
  assert.equal(await (await v2.request(path)).text(), 'map two');
  const v3 = loadWorker({ assets: assets.slice(0, 2), version: 'v3', caches: v1.caches, network: () => { throw new Error('offline'); } });
  await v3.install();
  await v3.activate();
  assert.equal(await (await v3.request(path)).text(), 'map two');
});

test('caches two releases back are removed once a worker activates', async () => {
  const v1 = loadWorker({ assets: assets.slice(0, 1), version: 'v1', network: () => ok('x') });
  await v1.install();
  const v2 = loadWorker({ assets: assets.slice(0, 1), version: 'v2', caches: v1.caches, network: () => ok('x') });
  await v2.install();
  const v3 = loadWorker({ assets: assets.slice(0, 1), version: 'v3', caches: v1.caches, network: () => ok('x') });
  await v3.install();
  await v3.activate();
  const keys = await v1.caches.keys();
  assert.ok(!keys.includes('offline-assets-v1'));
  assert.ok(keys.includes('offline-assets-v2') && keys.includes('offline-assets-v3'));
});

test('a navigation after the update is answered by the network, not the old shell', async () => {
  const v1 = loadWorker({ assets, version: 'v1', network: () => ok('shell v1') });
  await v1.install();
  const v2 = loadWorker({ assets, version: 'v2', caches: v1.caches, network: () => ok('shell v2') });
  await v2.install();
  await v2.activate();
  assert.equal(await (await v2.request('/', { mode: 'navigate' })).text(), 'shell v2');
});
