import assert from 'node:assert/strict';
import { after, before, test } from 'node:test';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
import { capturePageErrors, openTenantContexts, startPublishedPlatformHost } from './src/browser-harness.mjs';
import { startBrowserFixture } from './src/browser-fixture.mjs';

const testDirectory = dirname(fileURLToPath(import.meta.url));
const publishedDirectory = process.env.PLATFORM_BROWSER_HOST_DIRECTORY
  ?? resolve(testDirectory, 'test-fixtures/published-platform');
const runsPublishedClient = process.env.PLATFORM_BROWSER_HOST_DIRECTORY !== undefined;

const tenants = {
  'northwind.localhost': {
    config: {
      name: 'Northwind',
      languages: [
        { code: 'en', displayName: 'English', direction: 'ltr' },
        { code: 'fa', displayName: 'فارسی', direction: 'rtl' }
      ],
      defaultLanguage: 'fa',
      capabilities: {},
      configVersion: 7,
      serverBuildId: 'test'
    },
    themeColor: '#114477',
    theme: ':root { --shab: #114477; --lajevard: #163766; --lajevard-up: #1d4d82; --firouzeh: #55ccbb; }'
  },
  'contoso.localhost': {
    config: {
      name: 'Contoso',
      languages: [{ code: 'fa', displayName: 'فارسی', direction: 'rtl', appName: 'کویشی کنتوسو' }],
      defaultLanguage: 'fa',
      capabilities: {},
      configVersion: 11,
      serverBuildId: 'test'
    },
    themeColor: '#5d1f50',
    theme: ':root { --shab: #5d1f50; --lajevard: #762860; --lajevard-up: #923674; --firouzeh: #f0ae58; }'
  }
};

let browser;

before(async () => {
  browser = await chromium.launch({ headless: true });
});

after(async () => {
  await browser.close();
});

test('published platform shell is byte-identical across tenant hosts', async () => {
  const host = await startPublishedPlatformHost({ directory: publishedDirectory, tenants });
  const tenantContexts = await openTenantContexts(browser, host, ['northwind.localhost', 'contoso.localhost']);
  try {
    const responses = await Promise.all(tenantContexts.map(tenant => tenant.page.goto(tenant.url)));
    assert.ok(responses.every(response => response?.ok()), 'Each tenant host should serve the published platform shell.');
    assert.equal(await responses[0].text(), await responses[1].text());
    await tenantContexts[0].page.evaluate(() => localStorage.setItem('tenant', 'northwind'));
    assert.equal(await tenantContexts[1].page.evaluate(() => localStorage.getItem('tenant')), null);
  } finally {
    await Promise.all(tenantContexts.map(tenant => tenant.context.close()));
    await host.close();
  }
});

test('published client uses each tenant configuration online and after an offline revisit', { skip: !runsPublishedClient }, async () => {
  const host = await startPublishedPlatformHost({ directory: publishedDirectory, tenants });
  const context = await browser.newContext({ locale: 'en-US' });
  const page = await context.newPage();
  const assertNoPageErrors = capturePageErrors(page);
  try {
    await page.goto(host.tenantUrl('northwind.localhost'));
    await page.locator('h1').waitFor();
    assert.equal(await page.locator('h1').textContent(), 'Northwind');
    assert.equal(await page.evaluate(() => document.documentElement.dir), 'ltr');
    assert.equal(await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--shab').trim()), '#114477');

    await page.locator('select').selectOption('fa');
    await page.waitForFunction(() => document.documentElement.dir === 'rtl');
    assert.equal(await page.evaluate(() => localStorage.getItem('quesshi.lang')), 'fa');
    assert.match(await page.evaluate(() => document.cookie), /(?:^|; )lang=fa(?:;|$)/);

    await page.reload();
    await page.locator('h1').waitFor();
    assert.equal(await page.evaluate(() => document.documentElement.dir), 'rtl');

    await page.goto(host.tenantUrl('contoso.localhost'));
    await page.locator('h1').waitFor();
    await page.waitForFunction(() => navigator.serviceWorker.controller !== null);
    assert.equal(await page.locator('h1').textContent(), 'کویشی کنتوسو');
    assert.equal(await page.evaluate(() => document.documentElement.dir), 'rtl');
    await page.reload();
    await page.locator('h1').waitFor();

    await context.setOffline(true);
    await page.goto(host.tenantUrl('contoso.localhost'));
    await page.locator('h1').waitFor();
    assert.equal(await page.locator('h1').textContent(), 'کویشی کنتوسو');
    assert.equal(await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--shab').trim()), '#5d1f50');
    assert.equal(await page.evaluate(() => document.documentElement.dir), 'rtl');
    assertNoPageErrors();
  } finally {
    await context.close();
    await host.close();
  }
});

test('service worker supports online visit and offline revisit without page errors', async () => {
  const fixture = await startBrowserFixture();
  const context = await browser.newContext();
  const page = await context.newPage();
  const assertNoPageErrors = capturePageErrors(page);
  try {
    await page.goto(fixture.url);
    await page.waitForFunction(() => document.body.dataset.serviceWorker === 'ready');
    await page.reload();
    await context.setOffline(true);
    await page.goto(fixture.url);
    await assert.doesNotReject(() => page.locator('#status').waitFor());
    assertNoPageErrors();
  } finally {
    await context.close();
    await fixture.close();
  }
});

test('uncaught page errors fail the browser assertion', async () => {
  const context = await browser.newContext();
  const page = await context.newPage();
  const assertNoPageErrors = capturePageErrors(page);
  try {
    const pageError = page.waitForEvent('pageerror');
    await page.setContent('<script>setTimeout(() => { throw new Error("fixture failure") })</script>');
    await pageError;
    assert.throws(assertNoPageErrors, /uncaught error/);
  } finally {
    await context.close();
  }
});
