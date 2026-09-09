// Caution! Be sure you understand the caveats before publishing an application with
// offline support. See https://aka.ms/blazor-offline-considerations

self.importScripts('./service-worker-assets.js');
self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));

const cacheNamePrefix = 'offline-cache-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;
const offlineAssetsInclude = [ /\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.json$/, /\.css$/, /\.woff$/, /\.png$/, /\.jpe?g$/, /\.gif$/, /\.ico$/, /\.blat$/, /\.dat$/, /\.webmanifest$/ ];
const offlineAssetsExclude = [ /^service-worker\.js$/ ];

// Replace with your base path if you are hosting on a subfolder. Ensure there is a trailing '/'.
const base = "/";
const baseUrl = new URL(base, self.origin);
const manifestUrlList = self.assetsManifest.assets.map(asset => new URL(asset.url, baseUrl).href);

async function onInstall(event) {
    console.info('Service worker: Install');

    // Take over as soon as the new bundle is cached. Without this the default worker sits in
    // "waiting" until every tab for the site is closed, so a deploy appears to do nothing —
    // people reload, see the old app, and reasonably conclude the change did not ship.
    self.skipWaiting();

    // Cache the bundle asset by asset rather than with cache.addAll.
    //
    // addAll is atomic: one asset that 404s or whose integrity hash does not match rejects the whole
    // batch, install fails, and the new worker never activates — leaving the previous one serving the
    // previous app forever, with no way for a reload to break the loop. Caching each asset on its own
    // means a bad one costs exactly itself; onFetch falls through to the network for anything missing,
    // so the app still runs and only its offline completeness suffers.
    const assetsRequests = self.assetsManifest.assets
        .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
        .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)))
        .map(asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }));

    const cache = await caches.open(cacheName);
    const failed = [];
    await Promise.all(assetsRequests.map(async request => {
        try {
            await cache.put(request, await fetch(request));
        } catch {
            failed.push(request.url);
        }
    }));

    if (failed.length) {
        console.warn(`Service worker: ${failed.length} asset(s) not cached; they will be fetched from the network.`, failed);
    }
}

async function onActivate(event) {
    console.info('Service worker: Activate');

    // Delete unused caches
    const cacheKeys = await caches.keys();
    await Promise.all(cacheKeys
        .filter(key => key.startsWith(cacheNamePrefix) && key !== cacheName)
        .map(key => caches.delete(key)));

    // Serve already-open pages from the new cache too, so one reload is enough.
    await self.clients.claim();
}

async function onFetch(event) {
    if (event.request.method !== 'GET') return fetch(event.request);

    const isNavigation = event.request.mode === 'navigate'
        && !manifestUrlList.some(url => url === event.request.url);

    // Navigations go to the network first, and only fall back to the cached shell when that fails.
    //
    // Cache-first here is how a deploy can strand somebody indefinitely. onInstall caches the whole
    // bundle with cache.addAll and per-asset integrity hashes, so a single asset that 404s, or whose
    // hash does not match, rejects the whole batch: the new worker never activates and the old one
    // keeps serving the old index.html — and its asset hashes with it — on every future visit. There
    // is no reload out of that, because the reload is answered from the same cache. The app then
    // looks merely "out of date" while quietly talking to a server whose contracts have moved on,
    // which reads to a player as buttons that do nothing.
    //
    // index.html is small and names every hashed asset, so fetching it fresh is what lets a browser
    // recover on its own. Offline still works: the cached shell is right there when the network is not.
    if (isNavigation) {
        try {
            return await fetch(event.request);
        } catch {
            const cache = await caches.open(cacheName);
            return (await cache.match('index.html')) || Response.error();
        }
    }

    // Everything else stays cache-first: those URLs are content-hashed, so a hit is never stale.
    const cache = await caches.open(cacheName);
    return (await cache.match(event.request)) || fetch(event.request);
}
