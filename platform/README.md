# Quesshi Platform

This is the second, isolated Quesshi solution. It is being built beside the
current application so that the platform can establish its project boundaries,
plugin contracts, and tests without changing today's production application.

Nothing under `platform/` is deployed to production until the planned cut-over.

## Build and test

The platform requires the .NET 10 SDK.

```sh
dotnet restore platform/Quesshi.Platform.slnx
dotnet build platform/Quesshi.Platform.slnx --no-restore --configuration Release --warnaserror
dotnet test platform/Quesshi.Platform.slnx --no-build --configuration Release
```

Database tests use an admin PostgreSQL connection string from
`QUESSHI_TEST_POSTGRES`. For new database tests, the default is a
class-isolated `IClassFixture<PostgresFixture>`: the fixture creates a distinct
temporary database for that test class and drops it when the class finishes.
The `PostgreSQL` collection remains only for existing legacy collection-based
tests; new tests should not join it merely to obtain a database. See
[`tests/README.md`](tests/README.md) for the integration-test patterns.

Without that variable, database tests are skipped during local development.
When `CI=true`, the variable is required and a missing value fails the test run.
For example, with a local PostgreSQL server:

```sh
QUESSHI_TEST_POSTGRES='Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=postgres' \
  dotnet test platform/Quesshi.Platform.slnx --configuration Release
```

The `Platform` workflow runs the same restore, warning-free build, and test
commands with PostgreSQL 18. It performs validation only; it does not release
or deploy this tree.
