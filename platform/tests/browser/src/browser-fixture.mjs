import { createServer } from 'node:http';

const page = `<!doctype html>
<title>Platform browser fixture</title>
<main id="status">online</main>
<script type="module">
  await navigator.serviceWorker.register('/service-worker.js');
  await navigator.serviceWorker.ready;
  document.body.dataset.serviceWorker = 'ready';
</script>`;

const worker = `const cacheName = 'platform-browser-fixture-v1';
self.addEventListener('install', event => event.waitUntil(caches.open(cacheName).then(cache => cache.add('/'))));
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));
self.addEventListener('fetch', event => event.respondWith(caches.match(event.request).then(hit => hit || fetch(event.request))));`;

export async function startBrowserFixture() {
  const server = createServer((request, response) => {
    if (request.url === '/service-worker.js') {
      response.writeHead(200, { 'content-type': 'text/javascript', 'cache-control': 'no-store' }).end(worker);
      return;
    }
    if (request.url === '/') {
      response.writeHead(200, { 'content-type': 'text/html', 'cache-control': 'no-store' }).end(page);
      return;
    }
    response.writeHead(404).end();
  });
  await new Promise((resolveListen, reject) => {
    server.once('error', reject);
    server.listen({ host: '127.0.0.1', port: 0 }, resolveListen);
  });
  const { port } = server.address();
  return {
    url: `http://127.0.0.1:${port}/`,
    async close() {
      await new Promise(resolveClose => server.close(resolveClose));
    }
  };
}
