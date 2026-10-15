import assert from 'node:assert/strict';
import { after, before, test } from 'node:test';
import { chromium } from 'playwright';
import { capturePageErrors, openTenantContexts, startPublishedPlatformHost } from './src/browser-harness.mjs';

const publishedDirectory = process.env.PLATFORM_BROWSER_HOST_DIRECTORY;
const runsPublishedClient = publishedDirectory !== undefined;

const tenants = {
  'northwind.localhost': {
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

let browser;

before(async () => {
  browser = await chromium.launch({ headless: true });
});

after(async () => {
  await browser.close();
});

test('published non-map shell does not request map_pick assets', { skip: !runsPublishedClient }, async () => {
  const host = await startPublishedPlatformHost({ directory: publishedDirectory, tenants });
  const [tenant] = await openTenantContexts(browser, host, ['northwind.localhost']);
  const mapRequests = [];
  tenant.page.on('request', request => {
    if (request.url().includes('Quesshi.Kinds.MapPick.Client')) mapRequests.push(request.url());
  });

  try {
    await tenant.page.goto(tenant.url);
    await tenant.page.locator('h1').waitFor();
    assert.deepEqual(mapRequests, []);
  } finally {
    await tenant.context.close();
    await host.close();
  }
});

test('published map smoke loads and reuses lazy map resources for every map host', { skip: !runsPublishedClient }, async () => {
  const host = await startPublishedPlatformHost({ directory: publishedDirectory, tenants });
  const [tenant] = await openTenantContexts(browser, host, ['northwind.localhost']);
  const assertNoPageErrors = capturePageErrors(tenant.page);
  const mapClientRequests = [];
  const worldMapRequests = [];

  tenant.page.on('request', request => {
    const path = new URL(request.url()).pathname;
    if (path.includes('Quesshi.Kinds.MapPick.Client') && !path.endsWith('/maps/world.svg')) {
      mapClientRequests.push(path);
    }

    if (path.endsWith('/_content/Quesshi.Kinds.MapPick.Client/maps/world.svg')) {
      worldMapRequests.push(path);
    }
  });

  try {
    await tenant.page.goto(tenant.url + '?map-pick-smoke=1');
    await tenant.page.waitForFunction(() => !document.querySelector('[data-map-pick-browser-smoke] button')?.hasAttribute('@onclick'));
    const toggle = tenant.page.getByRole('button', { name: 'Render map components' });
    await toggle.waitFor();

    assert.deepEqual(mapClientRequests, []);
    assert.deepEqual(worldMapRequests, []);

    const worldMapResponse = tenant.page.waitForResponse(response =>
      new URL(response.url()).pathname.endsWith('/_content/Quesshi.Kinds.MapPick.Client/maps/world.svg') &&
      response.status() === 200);
    await toggle.click();
    await worldMapResponse;

    for (const hostName of ['play', 'reveal', 'editor']) {
      await tenant.page.locator('[data-map-pick-component="' + hostName + '"] [data-kind="map_pick"]').waitFor();
    }
    await tenant.page.locator('[data-map-pick-component] .worldmap path').first().waitFor();

    assert.ok(mapClientRequests.length > 0, 'first map use should request the lazy client assembly');
    assert.equal(worldMapRequests.length, 1, 'first map use should request the world map asset once');
    const initialMapClientRequestCount = mapClientRequests.length;

    await tenant.page.getByRole('button', { name: 'Hide map components' }).click();
    await tenant.page.locator('[data-map-pick-component]').first().waitFor({ state: 'detached' });

    await tenant.page.getByRole('button', { name: 'Render map components' }).click();
    await tenant.page.locator('[data-map-pick-component] .worldmap path').first().waitFor();

    assert.equal(mapClientRequests.length, initialMapClientRequestCount, 'later map use should reuse the lazy assembly');
    assert.equal(worldMapRequests.length, 1, 'later map use should reuse the world map asset');
    assertNoPageErrors();
  } finally {
    await tenant.context.close();
    await host.close();
  }
});
