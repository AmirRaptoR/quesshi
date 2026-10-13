import { createServer } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { extname, normalize, resolve, sep } from 'node:path';

const contentTypes = new Map([
  ['.css', 'text/css'],
  ['.html', 'text/html'],
  ['.js', 'text/javascript'],
  ['.json', 'application/json'],
  ['.wasm', 'application/wasm']
]);

function contentType(path) {
  return contentTypes.get(extname(path)) ?? 'application/octet-stream';
}

function fileForRequest(directory, requestUrl) {
  const pathname = new URL(requestUrl, 'http://localhost').pathname;
  const relativePath = normalize(decodeURIComponent(pathname === '/' ? '/index.html' : pathname)).replace(/^[/\\]+/, '');
  const candidate = resolve(directory, relativePath);
  if (candidate !== directory && !candidate.startsWith(`${directory}${sep}`)) throw new Error('Requested path is outside the published directory.');
  return candidate;
}

async function serveFile(directory, request, response) {
  try {
    const path = fileForRequest(directory, request.url);
    if (!(await stat(path)).isFile()) throw new Error('Not a file');
    response.writeHead(200, { 'content-type': contentType(path), 'cache-control': 'no-store' });
    response.end(await readFile(path));
  } catch {
    response.writeHead(404).end();
  }
}

async function listen(server) {
  await new Promise((resolveListen, reject) => {
    server.once('error', reject);
    server.listen({ host: '127.0.0.1', port: 0 }, resolveListen);
  });
  const { port } = server.address();
  return port;
}

export async function startPublishedPlatformHost({ directory }) {
  const rootDirectory = resolve(directory);
  const server = createServer((request, response) => void serveFile(rootDirectory, request, response));
  const port = await listen(server);
  return {
    port,
    tenantUrl(tenantHost, path = '/') {
      return `http://${tenantHost}:${port}${path}`;
    },
    async close() {
      await new Promise(resolveClose => server.close(resolveClose));
    }
  };
}

export async function openTenantContexts(browser, host, tenantHosts) {
  return Promise.all(tenantHosts.map(async tenantHost => {
    const context = await browser.newContext();
    return { context, page: await context.newPage(), tenantHost, url: host.tenantUrl(tenantHost) };
  }));
}

export function capturePageErrors(page) {
  const errors = [];
  page.on('pageerror', error => errors.push(error));
  return () => {
    if (errors.length > 0) throw new AggregateError(errors, `Page raised ${errors.length} uncaught error(s).`);
  };
}
