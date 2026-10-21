# Platform test guide

Platform integration tests use the shared helpers. They exercise the migrated
PostgreSQL database and the real ASP.NET host; do not replace either boundary
with a mock, an in-memory database, or a manually assembled HTTP request.

`Quesshi.Testing` owns `PostgresFixture` and `MediaSamples`, which test
projects may reference. `Quesshi.Server.Tests` owns `PlatformHttpFixture`,
the internal `RoleMatrix`, `DiscriminatingSeeds`, and the other `TestSeeds`.
HTTP, role-matrix, and discriminating-seed tests therefore belong in
`Quesshi.Server.Tests`; do not try to reference those test-local helpers from
another test project.

`QUESSHI_TEST_POSTGRES` is an administrator connection string. Database tests
skip locally when it is absent, but it is required when `CI=true`; a locally
skipped test is not evidence for a database criterion.

## PostgreSQL tests

Reference `Quesshi.Testing` and make each new database test class an
`IClassFixture<PostgresFixture>`. A fixture creates one temporary
`quesshi_test_*` database for that class and drops it afterwards, so classes
can run independently without sharing state. The `PostgreSQL` collection is a
legacy collection fixture; do not add new tests to it just to obtain a
database.

Migrate the temporary database, then call the production service or repository
and assert its observed result. Do not prove a database behavior by issuing a
second, hand-written query that duplicates the production SQL.

```csharp
public sealed class ContentRepositoryDatabaseTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task Category_lookup_uses_the_migrated_database()
    {
        Skip.If(postgres.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(postgres.ConnectionStringForRole("quesshi_owner"));

        await using var sources = new DatabaseSources(
            postgres.ConnectionStringForRole("quesshi_app"),
            postgres.ConnectionStringForRole("quesshi_system"));
        await using var app = Program.CreateApplication(
            sources,
            siloPort: uniqueSiloPort,
            gatewayPort: uniqueGatewayPort);
        await using var owner = new NpgsqlConnection(postgres.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        var tenant = await TenantSeed.CreateAsync(owner, "category-test");
        var category = await CategorySeed.CreateAsync(owner, tenant, "general");
        var expected = await QuestionSeed.CreateAsync(owner, tenant, category, "approved");
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();

        var question = await database.InTenantAsync(
            tenant.Id,
            transaction => content.GetAsync(transaction, expected.Id));

        Assert.NotNull(question);
    }
}
```

`uniqueSiloPort` and `uniqueGatewayPort` are deliberately placeholders: if a
database test starts `Program.CreateApplication`, choose a silo/gateway pair
that no other test uses. First search `platform/tests` for the proposed
numbers and reserve a distinct pair for the class. Prefer `PlatformHttpFixture`
for a hosted endpoint test; it allocates every host port dynamically.

Use the application's seed helpers to prepare state and the same role-specific
connection string that production uses: `quesshi_owner` for setup/migrations,
`quesshi_app` for tenant work, and `quesshi_system` for system work.

## Endpoint tests

Server endpoint tests use `PlatformHttpFixture`, not a hand-started server or
fixed ports. It starts a migrated real host on dynamically allocated HTTP,
silo, and gateway ports, with a separate temporary database and seeded clients
for `Anonymous`, `Guest`, `Player`, `Moderator`, `Editor`, `Owner`, and
`PlatformAdministrator`.

The fixture's clients already carry the appropriate identity cookie, tenant
host, `Origin`, and antiforgery header/token. `host.TenantOrigin` is the
tenant's origin, and `CreateOtherTenantPlayerClientAsync` supplies a client in
another tenant for isolation cases. Do not add these headers or issue tokens in
the test itself.

```csharp
public sealed class ExampleEndpointTests : IAsyncLifetime
{
    private PlatformHttpFixture? _host;
    private static readonly bool IsPostgresConfigured = PostgresFixture.IsConfigured(
        Environment.GetEnvironmentVariable(PostgresFixture.AdminConnectionStringEnvironmentVariable),
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase));

    public async Task InitializeAsync()
    {
        if (!IsPostgresConfigured) return;
        _host = new PlatformHttpFixture([new ExampleEndpointFeature()]);
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Editor_can_update_the_example()
    {
        Skip.If(!IsPostgresConfigured, "PostgreSQL is not configured.");

        using var response = await Host.Editor.PostAsync("/api/admin/x/example", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private PlatformHttpFixture Host => _host
        ?? throw new InvalidOperationException("The test host did not start.");
}
```

For a normal player route, use `host.Player`; for an unauthenticated path, use
`host.Anonymous`. For tenant isolation, send the same request through the
client returned by `CreateOtherTenantPlayerClientAsync` and assert that the
foreign tenant cannot observe or modify the primary tenant's data.

### Permission matrices

Express an endpoint's complete authorization contract with the shared role
matrix instead of testing only the successful role. The existing
editor-only contract is:

```csharp
await RoleMatrix.ForEditorOnlyMutation(host)
    .AssertAsync(client => client.PostAsync("/api/admin/x/example", null));
```

It runs the one endpoint for anonymous, guest, player, moderator, editor,
owner, and platform-administrator clients, expecting `401`, `401`, `401`,
`403`, `200`, `200`, and `200` respectively. Add a named matrix factory when
an endpoint has a different permission contract; keep the expected status for
each role explicit.

## Discriminating data

Every selection, filter, and isolation test needs a negative control that a
broken implementation would return. Seed both the expected data and data that
looks eligible except for the condition under test, then assert that the real
application excludes the latter. For example, the shared seed creates a
foreign tenant question that would reveal a missing tenant predicate:

```csharp
var primary = await TenantSeed.CreateAsync(owner, "primary");
var foreign = await DiscriminatingSeeds.AddForeignTenantContentAsync(owner, "foreign");

var question = await database.InTenantAsync(
    primary.Id,
    transaction => content.GetAsync(transaction, foreign.Question.Id));

Assert.Null(question);
```

`DiscriminatingSeeds` also provides an eligible question in an unselected
category, an ineligible question that sorts first, a finished-session seed,
and foreign media. Choose the control that would fail if the selection or
tenant-isolation condition were omitted.

## Media samples

`Quesshi.Testing.MediaSamples` exposes the checked-in, embedded files through
`MediaSamples.All` and reads their original bytes with `MediaSamples.ReadBytes`.
Use this corpus rather than ad-hoc binary data when a test needs a known media
type.

| File | Expected kind | Expected MIME type |
| --- | --- | --- |
| `sample.jpg` | `MediaKind.Image` | `image/jpeg` |
| `sample.png` | `MediaKind.Image` | `image/png` |
| `sample.webp` | `MediaKind.Image` | `image/webp` |
| `sample.mp3` | `MediaKind.Audio` | `audio/mpeg` |
| `sample.ogg` | `MediaKind.Audio` | `audio/ogg` |
| `sample.wav` | `MediaKind.Audio` | `audio/wav` |
| `sample.mp4` | `MediaKind.Video` | `video/mp4` |
| `sample.webm` | `MediaKind.Video` | `video/webm` |
