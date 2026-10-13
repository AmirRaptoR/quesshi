# Platform browser tests

The harness serves a published platform directory locally and creates one
isolated Playwright browser context per tenant host. It can also model each
host's `/api/config`, `/theme.css`, and `/manifest.webmanifest` responses,
including ETag revalidation, so browser coverage exercises the tenant-aware
shell without changing the browser runner.

Run the smoke tests after publishing the web project:

```sh
npm ci --prefix platform/tests/browser
npx --prefix platform/tests/browser playwright install chromium
PLATFORM_BROWSER_HOST_DIRECTORY=platform/artifacts/browser-platform/wwwroot \
  npm test --prefix platform/tests/browser
```

When `PLATFORM_BROWSER_HOST_DIRECTORY` is omitted, the harness uses its small
committed fixture so the harness can be developed without publishing the app.
