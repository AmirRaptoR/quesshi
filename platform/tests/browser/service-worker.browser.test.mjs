import assert from 'node:assert/strict';
import { cp, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { after, before, test } from 'node:test';
import { chromium } from 'playwright';
import { openTenantContexts, startPublishedPlatformHost } from './src/browser-harness.mjs';

const publishedDirectory = process.env.PLATFORM_BROWSER_HOST_DIRECTORY;
const runsPublishedClient = publishedDirectory !== undefined;
const tenantHost = 'northwind.localhost';
const tenants = {
  [tenantHost]: {
    config: {
      name: 'Northwind',
      languages: [{ code: 'en', displayName: 'English', direction: 'ltr' }],
      defaultLanguage: 'en',
      capabilities: {},
      configVersion: 1,
      serverBuildId: 'test'
    },
    theme: ':root { --shab: #114477; }'
  }
};
const lazyAdmin = 'Quesshi.Web.Admin';
const lazyMap = 'Quesshi.Kinds.MapPick.Client';

let browser;
before(async () => { browser = await chromium.launch({ headless: true }); });
after(async () => { await browser.close(); });

async function openWithWorker(directory) {
  const host = await startPublishedPlatformHost({ directory, tenants });
  const [tenant] = await openTenantContexts(browser, host, [tenantHost]);
  // Context-level events include the requests the service worker itself sends to the network.
  const requests = [];
  tenant.context.on('request', request => requests.push({ url: request.url(), fromWorker: request.serviceWorker() !== null }));
  await tenant.page.goto(tenant.url);
  await tenant.page.locator('h1').waitFor();
  await tenant.page.evaluate(async () => {
    await navigator.serviceWorker.ready;
    while (!navigator.serviceWorker.controller) await new Promise(resolve => setTimeout(resolve, 50));
  });
  return { host, tenant, requests };
}

const networkHits = (requests, part) => requests.filter(request => request.url.includes(part) && request.fromWorker).length;

test('with the worker active, lazy assemblies and the world map are fetched only when used, then served from the worker cache', { skip: !runsPublishedClient }, async () => {
  const { host, tenant, requests } = await openWithWorker(publishedDirectory);
  const { page } = tenant;
  try {
    await page.waitForTimeout(1500); // let the install-time fetches finish
    assert.deepEqual(requests.filter(r => r.url.includes(lazyAdmin) || r.url.includes(lazyMap)), [], 'first visit');

    await page.goto(host.tenantUrl(tenantHost, '/admin/login'));
    await page.locator('#admin-login-title').waitFor();
    assert.equal(networkHits(requests, lazyAdmin), 1, 'admin assembly fetched once on first use');

    await page.goto(tenant.url + '?map-pick-smoke=1');
    await page.getByRole('button', { name: 'Render map components' }).click();
    await page.locator('[data-map-pick-component] .worldmap path').first().waitFor();
    assert.equal(networkHits(requests, lazyMap + '.'), 1, 'map assembly fetched once on first use');
    assert.equal(networkHits(requests, 'maps/world.svg'), 1, 'world map fetched once on first use');

    const before = requests.length;
    await page.goto(host.tenantUrl(tenantHost, '/admin/login'));
    await page.locator('#admin-login-title').waitFor();
    await page.goto(tenant.url + '?map-pick-smoke=1');
    await page.getByRole('button', { name: 'Render map components' }).click();
    await page.locator('[data-map-pick-component] .worldmap path').first().waitFor();
    const later = requests.slice(before).filter(r => (r.url.includes(lazyAdmin) || r.url.includes(lazyMap)) && r.fromWorker);
    assert.deepEqual(later, [], 'second load is served from the worker cache');
  } finally {
    await tenant.context.close();
    await host.close();
  }
});

test('a tab left open across a release keeps working and runs the new version on its next navigation', { skip: !runsPublishedClient }, async () => {
  const directory = await mkdtemp(join(tmpdir(), 'quesshi-release-'));
  await cp(publishedDirectory, directory, { recursive: true });
  const { host, tenant } = await openWithWorker(directory);
  const { page } = tenant;
  try {
    // Deploy: new manifest version and shell. The old fingerprinted files stay beside the new ones.
    const manifestPath = join(directory, 'service-worker-assets.js');
    await writeFile(manifestPath, (await readFile(manifestPath, 'utf8')).replace(/"version": *"[^"]*"/, '"version": "release-2"'));
    const indexPath = join(directory, 'index.html');
    await writeFile(indexPath, (await readFile(indexPath, 'utf8')).replace('<head>', '<head><meta name="build" content="release-2" />'));

    await page.evaluate(async () => {
      const registration = await navigator.serviceWorker.getRegistration();
      await registration.update();
    });
    await page.waitForFunction(async () => (await caches.keys()).includes('offline-assets-release-2'));
    await page.waitForFunction(async () => {
      const registration = await navigator.serviceWorker.getRegistration();
      return registration.active && !registration.waiting && !registration.installing;
    });

    // The stale tab navigates inside the app and needs files only the old build's names describe.
    await page.evaluate(() => window.Blazor.navigateTo('/admin/login'));
    await page.locator('#admin-login-title').waitFor();

    await page.reload();
    await page.locator('h1').waitFor();
    assert.equal(await page.locator('meta[name=build]').getAttribute('content'), 'release-2');
  } finally {
    await tenant.context.close();
    await host.close();
    await rm(directory, { recursive: true, force: true });
  }
});
