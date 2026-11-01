self.importScripts('./service-worker-assets.js');

const assetCache = `offline-assets-${self.assetsManifest.version}`;
const lazyCache = `lazy-assets-${self.assetsManifest.version}`;
const dynamicCache = 'tenant-resources-v1';
// Lazy-loaded assemblies and the world map (served under the map client's static content path) stay
// out of the install list and are cached the first time a page asks for them.
const lazyPattern = /Quesshi\.Web\.Admin|Quesshi\.Kinds\.MapPick\.Client/;
const dynamicPaths = new Set(['/theme.css', '/manifest.webmanifest', '/api/config']);
const assetPatterns = [/\.dll$/, /\.wasm$/, /\.html$/, /\.js$/, /\.json$/, /\.css$/, /\.dat$/, /\.woff2?$/, /\.png$/, /\.ico$/];
const base = new URL('/', self.location.origin);
const assetUrls = self.assetsManifest.assets
  .filter(asset => !dynamicPaths.has(new URL(asset.url, base).pathname))
  .filter(asset => !lazyPattern.test(asset.url))
  .filter(asset => assetPatterns.some(pattern => pattern.test(asset.url)))
  .map(asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }));

// .NET references these boot modules by stable aliases, while the manifest
// contains their content-hashed modules. Cache both forms so an offline
// navigation can start the runtime before it imports the hashed modules.
for (const path of ['_framework/blazor.webassembly.js', '_framework/dotnet.js']) {
  assetUrls.push(new Request(path, { cache: 'no-cache' }));
}

self.addEventListener('install', event => event.waitUntil(install()));
self.addEventListener('activate', event => event.waitUntil(activate()));
self.addEventListener('fetch', event => event.respondWith(fetchRequest(event)));

async function install() {
  self.skipWaiting();
  const cache = await caches.open(assetCache);
  await Promise.all(assetUrls.map(async request => {
    try { await cache.put(request, await fetch(request)); } catch { /* network retry on first use */ }
  }));
}

// A tab left open across a release still asks for the previous build's fingerprinted files, so the
// newest superseded caches stay until the release after next. Caches are listed oldest first.
async function activate() {
  const keys = await caches.keys();
  const stale = key => keys.filter(k => k.startsWith(key) && k !== (key === 'offline-assets-' ? assetCache : lazyCache));
  const superseded = [...stale('offline-assets-').slice(0, -1), ...stale('lazy-assets-').slice(0, -1)];
  await Promise.all(superseded.map(key => caches.delete(key)));
  await self.clients.claim();
}

async function cachedAsset(request, current) {
  return (await current.match(request)) || (await caches.match(request));
}

async function lazyAsset(request) {
  const cache = await caches.open(lazyCache);
  const cached = await cachedAsset(request, cache);
  if (cached) return cached;
  const response = await fetch(request);
  if (response.ok) await cache.put(request, response.clone());
  return response;
}

function dynamicRequest(pathname) {
  return new Request(new URL(pathname, self.location.origin).href);
}

async function dynamicResource(request, pathname) {
  const cache = await caches.open(dynamicCache);
  const key = dynamicRequest(pathname);
  const cached = await cache.match(key);
  const headers = new Headers(request.headers);
  const etag = cached?.headers.get('etag');
  if (etag) headers.set('If-None-Match', etag);
  try {
    const response = await fetch(new Request(request, { headers, cache: 'no-store' }));
    if (response.status === 304 && cached) return cached;
    if (response.ok) await cache.put(key, response.clone());
    return response;
  } catch {
    return cached || Response.error();
  }
}

async function fetchRequest(event) {
  if (event.request.method !== 'GET') return fetch(event.request);
  const url = new URL(event.request.url);
  if (dynamicPaths.has(url.pathname)) return dynamicResource(event.request, url.pathname);
  const isNavigation = event.request.mode === 'navigate';
  if (isNavigation) {
    try { return await fetch(event.request); }
    catch {
      const cache = await caches.open(assetCache);
      return (await cache.match('index.html')) || Response.error();
    }
  }
  if (lazyPattern.test(url.pathname)) return lazyAsset(event.request);
  const cache = await caches.open(assetCache);
  return (await cachedAsset(event.request, cache)) || fetch(event.request);
}
