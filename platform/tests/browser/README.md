# Platform browser tests

The harness serves a published platform directory locally and creates one
isolated Playwright browser context per tenant host. Later browser tests can
use `startPublishedPlatformHost` with a published `wwwroot` directory and
`openTenantContexts` with the tenant hostnames they need.

Run the smoke tests after publishing the web project:

```sh
npm ci --prefix platform/tests/browser
npx --prefix platform/tests/browser playwright install chromium
PLATFORM_BROWSER_HOST_DIRECTORY=platform/artifacts/browser-platform/wwwroot \
  npm test --prefix platform/tests/browser
```

When `PLATFORM_BROWSER_HOST_DIRECTORY` is omitted, the harness uses its small
committed fixture so the harness can be developed without publishing the app.
