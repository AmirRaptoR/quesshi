import assert from 'node:assert/strict';
import { after, before, test } from 'node:test';
import { chromium } from 'playwright';
import { capturePageErrors, openTenantContexts, startPublishedPlatformHost } from './src/browser-harness.mjs';

const publishedDirectory = process.env.PLATFORM_BROWSER_HOST_DIRECTORY;
const runsPublishedClient = publishedDirectory !== undefined;

const tenants = {
  'admin-en.localhost': {
    config: {
      name: 'Admin tenant',
      languages: [{ code: 'en', displayName: 'English', direction: 'ltr' }],
      defaultLanguage: 'en',
      capabilities: {},
      configVersion: 1,
      serverBuildId: 'test'
    },
    theme: ':root { --shab: #114477; }'
  },
  'admin-fa-login.localhost': {
    config: {
      name: 'مدیریت',
      languages: [{ code: 'fa', displayName: 'فارسی', direction: 'rtl' }],
      defaultLanguage: 'fa',
      capabilities: {},
      configVersion: 2,
      serverBuildId: 'test'
    },
    theme: ':root { --shab: #5d1f50; }'
  },
  'admin-fa-owner.localhost': {
    config: {
      name: 'مدیریت',
      languages: [{ code: 'fa', displayName: 'فارسی', direction: 'rtl' }],
      defaultLanguage: 'fa',
      capabilities: {},
      configVersion: 3,
      serverBuildId: 'test'
    },
    theme: ':root { --shab: #5d1f50; }',
    resources: {
      '/api/admin/auth/me': { body: '{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","role":"Owner","isPlatform":false,"mustChangePassword":false}', type: 'application/json', tag: 'owner' },
      '/api/admin/accounts': { body: '[]', type: 'application/json', tag: 'accounts' }
    }
  }
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

test('published player routes do not fetch the lazy admin client', { skip: !runsPublishedClient }, async () => {
  const host = await startPublishedPlatformHost({ directory: publishedDirectory, tenants });
  const [tenant] = await openTenantContexts(browser, host, ['admin-en.localhost']);
  const adminRequests = [];
  tenant.page.on('request', request => {
    if (request.url().includes('Quesshi.Web.Admin')) adminRequests.push(request.url());
  });

  try {
    await tenant.page.goto(tenant.url);
    await tenant.page.locator('h1').waitFor();

    assert.deepEqual(adminRequests, []);
  } finally {
    await tenant.context.close();
    await host.close();
  }
});

test('published admin routes load the client once and reuse it for another page', { skip: !runsPublishedClient }, async () => {
  const host = await startPublishedPlatformHost({ directory: publishedDirectory, tenants });
  const [tenant] = await openTenantContexts(browser, host, ['admin-en.localhost']);
  const assertNoPageErrors = capturePageErrors(tenant.page);
  const adminRequests = [];
  tenant.page.on('request', request => {
    if (request.url().includes('Quesshi.Web.Admin')) adminRequests.push(request.url());
  });

  try {
    await tenant.page.goto(host.tenantUrl('admin-en.localhost', '/admin/login'));
    await tenant.page.locator('#admin-login-title').waitFor();
    assert.ok(adminRequests.length > 0, 'The first admin route should request its lazy client.');
    const firstLoadRequests = adminRequests.length;

    await tenant.page.getByRole('link', { name: 'Forgot your password?' }).click();
    await tenant.page.locator('#admin-forgot-title').waitFor();

    assert.equal(adminRequests.length, firstLoadRequests, 'A second admin page should reuse the loaded client.');
    assertNoPageErrors();
  } finally {
    await tenant.context.close();
    await host.close();
  }
});

test('published right-to-left admin login password and account pages fit their controls', { skip: !runsPublishedClient }, async () => {
  const host = await startPublishedPlatformHost({ directory: publishedDirectory, tenants });
  const contexts = await openTenantContexts(browser, host, ['admin-fa-login.localhost', 'admin-fa-owner.localhost']);
  const [login, owner] = contexts;
  const assertLoginErrors = capturePageErrors(login.page);
  const assertOwnerErrors = capturePageErrors(owner.page);

  try {
    await login.page.goto(host.tenantUrl('admin-fa-login.localhost', '/admin/login'));
    await login.page.locator('#admin-login-title').waitFor();
    await assertRtlActionsFit(login.page);

    await owner.page.goto(host.tenantUrl('admin-fa-owner.localhost', '/admin/password'));
    await owner.page.locator('#admin-password-title').waitFor();
    await assertRtlActionsFit(owner.page);

    await owner.page.goto(host.tenantUrl('admin-fa-owner.localhost', '/admin/accounts'));
    await owner.page.locator('#admin-accounts-title').waitFor();
    await assertRtlActionsFit(owner.page);
    assertLoginErrors();
    assertOwnerErrors();
  } finally {
    await Promise.all(contexts.map(tenant => tenant.context.close()));
    await host.close();
  }
});
