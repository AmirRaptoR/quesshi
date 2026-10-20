using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Quesshi.Plugin.Api;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class RoleMatrixTests(PostgresFixture postgres) : IAsyncLifetime
{
    private PlatformHttpFixture? _host;

    public async Task InitializeAsync()
    {
        if (postgres.ConnectionString is null) return;

        _host = new PlatformHttpFixture([new EditorOnlyMutationFeature()]);
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Editor_only_mutation_observes_expected_status_for_each_identity()
    {
        await RoleMatrix.ForEditorOnlyMutation(Host)
            .AssertAsync(client => client.PostAsync("/api/admin/x/role_matrix/mutation", null));
    }

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(postgres.ConnectionString is null, "PostgreSQL is not configured.");
            return _host ?? throw new InvalidOperationException("The platform HTTP host did not start.");
        }
    }

    private sealed class EditorOnlyMutationFeature : IFeature
    {
        public string Id => "role_matrix";

        public FeatureSettings Settings => FeatureSettings.Create(new RoleMatrixSettings(), _ => []);

        public void Configure(IFeatureBuilder feature) =>
            feature.AdminRoutes(AdminRole.Editor, routes => routes.MapPost("/mutation", () => Results.Ok()));
    }

    private sealed record RoleMatrixSettings;
}
