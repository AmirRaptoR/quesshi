# Agent guide for Quesshi

Read this file before you change anything in this repository. It is written for coding agents.
Follow every **must** and **never** literally. When this file and the issue disagree, the issue
wins; say so in your result. When the issue and the platform design disagree, the design wins
(see 1.2); say so in the pull request.

## 1. Two codebases: decide which one the issue is about

This repository holds two separate .NET solutions.

| | Today's application | The platform |
| --- | --- | --- |
| Folders | `src/`, `tests/`, `deploy/`, root `compose.yaml`, `Dockerfile` | `platform/`, `docs/platform/` |
| Solution | `Quesshi.slnx` | `platform/Quesshi.Platform.slnx` |
| Status | Runs in production now | Being built beside it; not deployed until the cut-over |
| Stack | Orleans with Redis, MongoDB, SignalR | Orleans on PostgreSQL, Dapper, server-sent events |
| CI workflow | `CI` (`.github/workflows/ci.yml`) | `Platform` (`.github/workflows/platform.yml`) |

Most issues are platform issues. An issue is a platform issue when it says so, links
`docs/platform/architecture.md`, or says "build only under `platform/`". If you cannot tell, ask.

### 1.1 Rules for platform issues

- **Build only under `platform/`**, plus `docs/platform/` and `.github/workflows/platform*.yml`.
  Never modify `src/`, `tests/`, `deploy/`, the root `compose.yaml`, the root `Dockerfile` or
  `.github/workflows/ci.yml`. Today's application must keep building and deploying unchanged.
- **Copy, do not move.** When the issue says to reuse today's code or tests, copy them into the
  platform tree and adapt them. Today's projects still use the originals.
- **Today's behaviour wins.** Where the design and today's behaviour differ, keep today's
  behaviour unless the difference is listed in `docs/platform/architecture.md` section 19.4. If
  you find a difference that is not listed, stop and ask through a question.
- **The design is the specification.** `docs/platform/architecture.md` and
  `docs/platform/schema.sql` describe what to build. If the design is silent or looks wrong, ask
  rather than guess.

### 1.2 Reading the design

`docs/platform/architecture.md` is about 2,000 lines. Never read it whole. Read the sections the
issue names, and these when relevant:

| Section | Topic |
| --- | --- |
| 3 | Stack and database conventions |
| 4 | Solution layout and what may reference what |
| 5.4 | Rules for plugins, and the locking order for transactions |
| 6 / 8 | Question kinds / formats |
| 7.4, 7.5 | The session grain, time, deadlines and timers |
| 10.2 | Tenant isolation |
| 11 | Identity and security |
| 14 | Background jobs |
| 18 | Testing: what each area must prove |
| 19.4 | Deliberate differences from today |

## 2. Commands

### Platform

```sh
dotnet restore platform/Quesshi.Platform.slnx
dotnet build platform/Quesshi.Platform.slnx --no-restore --configuration Release --warnaserror
dotnet test platform/Quesshi.Platform.slnx --no-build --configuration Release
```

- One test project: `dotnet test platform/tests/Quesshi.Server.Tests/Quesshi.Server.Tests.csproj --configuration Release`.
- One test: add `--filter "FullyQualifiedName~Guest_upgrade_rejects_an_expired_code"`.
- **Database tests need PostgreSQL.** Set `QUESSHI_TEST_POSTGRES` to an admin connection string,
  for example `Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=postgres`.
  Without it, database tests are **skipped** locally, which proves nothing. A green run with
  skipped database tests is not evidence for a database criterion. When `CI=true` the variable is
  required.
- CI also runs `Quesshi.Server.Tests` with `-p:QuesshiTestIdentity=true`, which defines
  `QUESSHI_TEST_IDENTITY` in `Quesshi.Server`. Code behind that symbol must build and pass in both
  modes.
- Browser tests for the published client live in `platform/tests/browser` (Node and Playwright).
  CI publishes `Quesshi.Web` and runs them with `npm test --prefix platform/tests/browser`.

The build treats every warning as an error. Fix the warning; never suppress it with `#pragma`,
`NoWarn` or by lowering the analysis level.

### Today's application

```sh
dotnet restore Quesshi.slnx
dotnet build Quesshi.slnx --no-restore --configuration Release
dotnet test Quesshi.slnx --no-build --configuration Release
```

## 3. Platform layout and dependency rules

| Project (`platform/src/`) | Contents | May reference |
| --- | --- | --- |
| `Quesshi.Shared` | DTOs, views, cards, commands, the kind contract | nothing in the solution |
| `Quesshi.Domain` | Pure rules: scoring, positions, standings | nothing |
| `Quesshi.Kinds` | One folder per question kind (`Choice/`, `Order/`, `MapPick/`, …) | `Shared` only |
| `Quesshi.Engine.Contracts` | Format contract, in-memory session handle, `IClock`; pure types only | `Shared` only |
| `Quesshi.Formats` | One folder per format (`AsyncDuel/`, `Poll/`) | `Engine.Contracts`, `Domain`, `Shared` |
| `Quesshi.Plugin.Api` | What plugins may see of the core: `ITenantTx`, `ITenantDb`, `ISessions`, `IContent`, `ILocks`, `IInbox` | |
| `Quesshi.Features` | One folder per feature, sign-in method, content source | `Plugin.Api` only |
| `Quesshi.Application` | The core: tenancy, identity, content, session engine, events, jobs. Its types are `internal` | implements `Plugin.Api` |
| `Quesshi.Infrastructure` | PostgreSQL access, mail, storage, model provider, clock | |
| `Quesshi.Grains.Abstractions` / `Quesshi.Grains` | The session grain's interface / `SessionGrain` | |
| `Quesshi.Server` | Host: API, streams, silo, registry, migrate command (`Program.cs`) | |
| `Quesshi.Web`, `Quesshi.Web.Plugins`, `Quesshi.Web.Admin`, `Quesshi.Kinds.MapPick.Client` | Blazor WebAssembly client and its lazy-loaded libraries | |

These rules are enforced, and you must never work around them:

- **Project references.** Never add a project reference that the table does not allow. A
  feature never references `Application`; a format never references anything that can reach a
  database.
- **Banned APIs in `Kinds` and `Formats`** (`platform/BannedSymbols.txt`,
  `platform/PluginBannedNamespaces.txt`): no `System.Net`, `System.IO`, `System.Reflection.Emit`,
  `Npgsql`, `Random.Shared`, `DateTime.Now/UtcNow`, `DateTimeOffset.Now/UtcNow`,
  `Guid.NewGuid`, or dynamic assembly loading. Take time, randomness and ids from the caller.
- **No direct time source in production code** (`TimeSourceBoundaryTests`): never use
  `DateTime.UtcNow`, `DateTimeOffset.UtcNow` or `Stopwatch` anywhere under `platform/src` (the
  only exception is the clock implementation in `Quesshi.Infrastructure`). Use
  `IClock` (`Quesshi.Engine.Contracts`) or an injected `TimeProvider`.
- **Plugins never reference each other** (`PluginFolderReferenceTests`): no type in one plugin
  folder may use another plugin folder's namespace. Read another plugin's data only through a
  named read-only query it provides.
- **Never in the platform tree:** EF Core, Orleans grain storage providers, Orleans reminders,
  Redis, MongoDB, SignalR.

## 4. Database rules (platform)

- **SQL is written by hand with Dapper.** Run it inside `ITenantDb.InTenantAsync` (or the
  `ITenantTx` you were given). Every statement in a tenant transaction uses that transaction's
  connection. Never open your own connection or data source in feature or application code.
- **Conventions** (architecture section 3): ids are `Guid` / `uuid`. JSON columns are passed as
  typed `jsonb` parameters, never as strings, and read with `System.Text.Json` source-generated
  metadata. Lists are passed as arrays and matched with `= any(@ids)`.
- **Migrations** are plain SQL files, forward-only, in one stream ordered by file name:
  - core: `platform/src/Quesshi.Application/Migrations/`;
  - a feature: `platform/src/Quesshi.Features/<FeatureId>/Migrations/`;
  - name: `yyyyMMddTHHmm_<owner>_<what>.sql`, for example `20261015T0204_core_admin_identity_audit.sql`.
    The timestamp must sort after every existing migration.
  - **Never edit a migration that is already on `main`.** Add a new one.
- **Tenant tables** start with `tenant_id` and are registered with
  `select quesshi_tenant_table('<table>', '<owner>');`, which applies the policy and grants. A
  plugin's tables may reference core tables, never another plugin's tables.
- **Roles:** migrations run as `quesshi_owner`; the application uses `quesshi_app` (tenant data)
  and `quesshi_system` (cross-tenant system work). Grant only what the design's `schema.sql` grants.
- **Locking order** (architecture 5.4): lock shared rows only through `ILocks`, in this order and
  ascending by id within each step: players, then content, then the plugin's own rows.
- If your migration differs from `docs/platform/schema.sql`, update `schema.sql` (and the
  architecture document if the design changed) in the same pull request.

## 5. Tests (platform)

- Framework: **xUnit 2**, `Xunit.SkippableFact` for database tests, **bUnit** for Blazor
  components (`Quesshi.Web.Tests`).
- Test project per source area: `platform/tests/Quesshi.<Area>.Tests`. Shared fixtures are in
  `platform/tests/Quesshi.Testing` (`PostgresFixture`).
- **Test names are sentences in snake case** that state the behaviour:
  `Guest_upgrade_rejects_an_expired_code`, not `Test1` or `UpgradeTest`.
- **Database tests** belong to the `PostgreSQL` collection and skip when PostgreSQL is not
  configured. Follow this shape:

  ```csharp
  [Collection("PostgreSQL")]
  public sealed class GuestPurgeDatabaseTests(PostgresFixture fixture)
  {
      [SkippableFact]
      public async Task Purge_keeps_a_guest_with_a_seat_in_an_unfinished_session()
      {
          Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
          // connect with fixture.ConnectionStringForRole("quesshi_app") or ("quesshi_system")
      }
  }
  ```

  Each test project that needs PostgreSQL declares the collection once with
  `[CollectionDefinition("PostgreSQL", DisableParallelization = true)]` and
  `ICollectionFixture<PostgresFixture>`; reuse the existing definition in that project.
- **HTTP behaviour is tested through the real application.** Start it with
  `Program.CreateApplication(sources, siloPort, gatewayPort, ...)`, add a URL with
  `app.Urls.Add("http://127.0.0.1:<port>")`, `StartAsync()` it and call it with `HttpClient`
  (see `RouteSecurityDatabaseTests`). Pick silo, gateway and HTTP ports that no other test uses;
  search the test projects for the numbers first.
- **Tests must exercise the real code path.**
  - Never call `private` members through reflection (`GetMethod`, `GetField`,
    `BindingFlags.NonPublic`), never widen visibility for a test, and never add test-only
    branches to production code. `internal` types are visible to test projects through
    `InternalsVisibleTo`; that is the intended access.
  - Never mock or fake the unit under test, and never assert against a copy of its logic, such as
    hand-written SQL that imitates a service instead of calling the service.
  - If something can only be tested by reaching inside it, restructure the code so its
    behaviour is observable from outside.
  - `SessionGrainBehaviorTests.cs` and `SessionCommandEndpointTests.cs` still reach private
    members by reflection. That is debt to remove. Never copy the pattern.
- **One assertion per case.** When an acceptance criterion lists several cases (outcomes,
  error codes, roles, races), each case gets its own test or assertion. Criteria that say "in
  Platform CI" mean a test that runs in the `Platform` workflow; database criteria need tests
  that actually ran against PostgreSQL.
- **Concurrency and recovery claims need tests that force them**: two concurrent requests, a
  retry after a crash, a duplicate delivery, a lost connection. A single success-path test is
  not evidence for them.
- Copied tests from today's `tests/` keep their scenarios. Adapt them to the platform API; never
  drop a scenario silently. If one cannot apply, say why in the pull request.
- Never weaken, skip or delete a failing test to get a green run.

## 6. Code style

- C# with `Nullable` enabled and warnings as errors. Use file-scoped namespaces
  (`namespace Quesshi.Application.Jobs;`).
- Make classes `sealed` unless they are designed for inheritance. Prefer records for data
  carriers, and primary constructors where the surrounding code does.
- Async methods end in `Async` and accept and pass on a `CancellationToken`.
- Keep `Application` types `internal`. Make a type public only when another project's contract
  requires it.
- Validate input at the HTTP boundary and return the error shape the design names (for example
  `409 { error: "address_taken" }`). Never let a validation exception become a 500.
- Reuse existing helpers and patterns; search before writing a new one. Keep the change to what
  the issue asks.

## 7. Before you finish

1. The platform build (`--warnaserror`) and tests pass, **with `QUESSHI_TEST_POSTGRES` set** when
   you changed anything that touches the database.
2. For each acceptance criterion you can name the test that proves it, and that test runs the
   real code.
3. No file outside the allowed folders changed (for a platform issue: nothing outside
   `platform/`, `docs/platform/` and `.github/workflows/platform*.yml`).
4. New migrations are new files with a later timestamp; no existing migration changed.
5. `docs/platform/schema.sql` and `architecture.md` match what you built, or the pull request
   explains the difference.
6. No secrets, connection strings with real passwords, hostnames or personal data in code, tests
   or docs.
