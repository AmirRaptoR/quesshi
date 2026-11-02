// Reads the server-rendered landing and join pages in a real browser with JavaScript switched off.
// Run by the .NET StaticPageHostTests against a live server; it is not a node:test file.
import assert from 'node:assert/strict';
import { chromium } from 'playwright';

const host = process.env.PLATFORM_STATIC_PAGES_HOST;
const port = process.env.PLATFORM_STATIC_PAGES_PORT;
if (!host || !port) throw new Error('PLATFORM_STATIC_PAGES_HOST and PLATFORM_STATIC_PAGES_PORT are required.');

const browser = await chromium.launch({
  headless: true,
  args: [`--host-resolver-rules=MAP ${host} 127.0.0.1`]
});

async function open({ locale, cookie }) {
  const context = await browser.newContext({ javaScriptEnabled: false, locale });
  if (cookie) await context.addCookies([{ name: 'lang', value: cookie, url: `http://${host}:${port}/` }]);
  return { context, page: await context.newPage() };
}

async function check(name, options, body) {
  const { context, page } = await open(options);
  try {
    await body(page);
    console.log(`ok - ${name}`);
  } finally {
    await context.close();
  }
}

const url = path => `http://${host}:${port}${path}`;
const root = page => page.locator('html');

try {
  await check('landing is readable in the browser language, left to right', { locale: 'en-US' }, async page => {
    await page.goto(url('/'));
    assert.equal(await root(page).getAttribute('lang'), 'en');
    assert.equal(await root(page).getAttribute('dir'), 'ltr');
    assert.equal(await page.locator('h1').innerText(), 'Test Quiz');
    assert.ok(await page.locator('.hero__lede').isVisible());
    assert.ok(await page.getByText('Never online together').isVisible());
  });

  await check('landing follows the lang cookie, right to left', { locale: 'en-US', cookie: 'fa' }, async page => {
    await page.goto(url('/'));
    assert.equal(await root(page).getAttribute('lang'), 'fa');
    assert.equal(await root(page).getAttribute('dir'), 'rtl');
    assert.equal(await page.locator('h1').innerText(), 'آزمون');
  });

  await check('join page is readable with a join button and an empty app container', { locale: 'fa-IR' }, async page => {
    await page.goto(url('/join/ABC123'));
    assert.equal(await root(page).getAttribute('lang'), 'fa');
    assert.equal(await root(page).getAttribute('dir'), 'rtl');
    assert.ok(await page.locator('#preview #join-button').isVisible());
    assert.equal((await page.locator('#app').innerHTML()).trim(), '');
  });

  await check('join page falls back to the tenant default language', { locale: 'de-DE' }, async page => {
    await page.goto(url('/join/ABC123'));
    assert.equal(await root(page).getAttribute('lang'), 'en');
    assert.equal(await root(page).getAttribute('dir'), 'ltr');
    assert.ok(await page.locator('#join-button').isVisible());
  });
} finally {
  await browser.close();
}
