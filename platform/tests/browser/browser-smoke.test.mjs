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

function rtlTenant(resources = {}) {
  return {
    config: {
      name: 'Persian tenant',
      languages: [{ code: 'fa', displayName: 'فارسی', direction: 'rtl' }],
      defaultLanguage: 'fa',
      capabilities: {},
      configVersion: 19,
      serverBuildId: 'test'
    },
    themeColor: '#5d1f50',
    theme: ':root { --shab: #5d1f50; --lajevard: #762860; --lajevard-up: #923674; --firouzeh: #f0ae58; }',
    resources
  };
}

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
  },
  'rtl-sign-in.localhost': rtlTenant(),
  'rtl-confirmation.localhost': rtlTenant({
    '/api/antiforgery': { body: '{"token":"csrf"}', type: 'application/json', tag: 'csrf' },
    '/api/auth/code': { body: '{"status":"accepted"}', type: 'application/json', tag: 'code-requested' },
    '/api/auth/verify': { body: '{"status":"profile_needed","token":"proof","displayName":"بازیکن","avatarSeed":"avatar-7","lang":"fa"}', type: 'application/json', tag: 'profile-needed' }
  }),
  'rtl-player.localhost': rtlTenant({
    '/api/me': { body: '{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","displayName":"بازیکن","avatarSeed":"avatar-7","lang":"fa"}', type: 'application/json', tag: 'player' }
  })
};

let browser;

before(async () => {
  browser = await chromium.launch({ headless: true });
});

after(async () => {
  await browser.close();
});

async function assertRtlActionsFit(page) {
  assert.equal(await page.evaluate(() => document.documentElement.dir), 'rtl');
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true);
  assert.equal(await page.locator('button').evaluateAll(buttons => buttons.every(button => {
    const bounds = button.getBoundingClientRect();
    return bounds.left >= 0 && bounds.right <= window.innerWidth;
  })), true);
}

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
    assert.equal(await page.locator('h1').textContent(), 'Sign in');
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
    await page.waitForFunction(() => document.documentElement.dir === 'rtl');
    assert.equal(await page.locator('h1').textContent(), 'وارد شو');
    assert.equal(await page.evaluate(() => document.documentElement.dir), 'rtl');
    await page.reload();
    await page.locator('h1').waitFor();

    await context.setOffline(true);
    await page.goto(host.tenantUrl('contoso.localhost'));
    await page.locator('h1').waitFor();
    assert.equal(await page.locator('h1').textContent(), 'نشست تأیید نشد');
    assert.equal(await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--shab').trim()), '#5d1f50');
    assert.equal(await page.evaluate(() => document.documentElement.dir), 'rtl');
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true);
    assert.equal(await page.locator('button').evaluateAll(buttons => buttons.every(button => {
      const bounds = button.getBoundingClientRect();
      return bounds.left >= 0 && bounds.right <= window.innerWidth;
    })), true);
    assertNoPageErrors();
  } finally {
    await context.close();
    await host.close();
  }
});

test('published player entry, confirmation, profile, and settings fit their actions in RTL', { skip: !runsPublishedClient }, async () => {
  const host = await startPublishedPlatformHost({ directory: publishedDirectory, tenants });
  const context = await browser.newContext({ locale: 'fa-IR' });
  const page = await context.newPage();
  const assertNoPageErrors = capturePageErrors(page);
  try {
    await page.goto(host.tenantUrl('rtl-sign-in.localhost'));
    await page.locator('#sign-in-title').waitFor();
    await assertRtlActionsFit(page);

    await page.goto(host.tenantUrl('rtl-confirmation.localhost'));
    await page.locator('#email').fill('ada@example.test');
    await page.locator('button[type="submit"]').click();
    await page.locator('#code').fill('123456');
    await page.locator('button[type="submit"]').click();
    await page.locator('#confirm-profile-title').waitFor();
    await assertRtlActionsFit(page);

    await page.goto(host.tenantUrl('rtl-player.localhost'));
    await page.locator('#home-title').waitFor();
    await page.locator('main section button').click();
    await page.locator('#profile-title').waitFor();
    await assertRtlActionsFit(page);
    await page.locator('main section button').click();
    await page.locator('#settings-title').waitFor();
    await assertRtlActionsFit(page);
    assertNoPageErrors();
  } finally {
    await context.close();
    await host.close();
  }
});

test('published client renders every eager kind component through its hosts', { skip: !runsPublishedClient }, async () => {
  const host = await startPublishedPlatformHost({ directory: publishedDirectory, tenants });
  const context = await browser.newContext();
  const page = await context.newPage();
  const assertNoPageErrors = capturePageErrors(page);
  const components = [
    'choice-play', 'choice-reveal', 'choice-editor',
    'order-play', 'order-reveal', 'order-editor',
    'poll_choice-play', 'poll_choice-reveal', 'poll_choice-editor',
    'poll_roster-play', 'poll_roster-reveal', 'poll_roster-editor'
  ];
  try {
    await page.goto(host.tenantUrl('northwind.localhost', '/?eager-kind-smoke=1'));
    for (const component of components) {
      await page.locator(`[data-eager-component="${component}"] [data-kind]`).waitFor();
    }
    assert.equal(await page.locator('[data-eager-component] [role="alert"]').count(), 0);
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
