import assert from 'node:assert/strict';
import { after, before, test } from 'node:test';
import { chromium } from 'playwright';
import { openTenantContexts, startPublishedPlatformHost } from './src/browser-harness.mjs';

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
