import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const workerPath = fileURLToPath(new URL('../../../src/Quesshi.Web/wwwroot/service-worker.published.js', import.meta.url));
export const origin = 'https://northwind.example';

class FakeCache {
  entries = new Map();
  async put(request, response) { this.entries.set(new URL(request.url ?? request, origin).href, response); }
  async match(request) {
    const found = this.entries.get(new URL(request.url ?? request, origin).href);
    return found?.clone();
  }
}

class FakeCacheStorage {
  stores = new Map();
  async open(name) {
    if (!this.stores.has(name)) this.stores.set(name, new FakeCache());
    return this.stores.get(name);
  }
  async keys() { return [...this.stores.keys()]; }
  async delete(name) { return this.stores.delete(name); }
  async match(request) {
    for (const store of this.stores.values()) {
      const found = await store.match(request);
      if (found) return found;
    }
    return undefined;
  }
}

/**
 * Runs the real worker script against a manifest, with an in-memory cache storage shared across
 * workers so a release can be modelled as a second worker over the same caches.
 * `network(request)` answers every fetch the worker makes and may throw to model being offline.
 */
// Workers resolve relative request URLs against their own location; Node's Request does not.
class WorkerRequest extends Request {
  constructor(input, init) {
    super(typeof input === 'string' ? new URL(input, origin).href : input, init);
  }
}

export function loadWorker({ assets, version, network, caches = new FakeCacheStorage() }) {
  const listeners = {};
  const fetched = [];
  const self = {
    location: new URL(`${origin}/service-worker.js`),
    assetsManifest: { version, assets },
    importScripts: () => {},
    clients: { claim: async () => {} },
    skipWaiting: () => {},
    addEventListener: (type, listener) => { listeners[type] = listener; }
  };
  const context = vm.createContext({
    self, caches, URL, Request: WorkerRequest, Response, Headers, Promise, Set, RegExp,
    fetch: async input => {
      const request = input;
      fetched.push(new URL(request.url).pathname);
      return network(request);
    }
  });
  vm.runInContext(readFileSync(workerPath, 'utf8'), context);
  const dispatch = async (type, init) => {
    let pending;
    listeners[type]({ ...init, waitUntil: p => { pending = p; }, respondWith: p => { pending = p; } });
    return pending;
  };
  return {
    caches, fetched,
    install: () => dispatch('install'),
    activate: () => dispatch('activate'),
    request: (path, { mode, ...init } = {}) => dispatch('fetch', {
      // Node's Request refuses mode "navigate", so a navigation is a plain request-shaped object.
      request: mode === 'navigate'
        ? { method: 'GET', url: new URL(path, origin).href, mode, headers: new Headers() }
        : new Request(new URL(path, origin), init)
    })
  };
}
