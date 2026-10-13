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

let browser;

before(async () => {
  browser = await chromium.launch({ headless: true });
});

after(async () => {
  await browser.close();
});

test('published platform host provides isolated tenant contexts', async () => {
  const host = await startPublishedPlatformHost({ directory: publishedDirectory });
  const tenants = await openTenantContexts(browser, host, ['northwind.localhost', 'contoso.localhost']);
  try {
    const responses = await Promise.all(tenants.map(tenant => tenant.page.goto(`${tenant.url}_framework/blazor.webassembly.js`)));
    assert.ok(responses.every(response => response?.ok()), 'Each tenant host should serve the published platform assets.');
    await tenants[0].page.evaluate(() => localStorage.setItem('tenant', 'northwind'));
    assert.equal(await tenants[1].page.evaluate(() => localStorage.getItem('tenant')), null);
  } finally {
    await Promise.all(tenants.map(tenant => tenant.context.close()));
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
