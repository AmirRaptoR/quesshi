using System.Net;
using System.Net.Http.Json;
using Quesshi.Plugin.Api;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

public sealed class AdminDeadJobEndpointTests : IAsyncLifetime
{
    private const string DeadJobsPath = "/api/admin/dead-jobs";
    private readonly PlatformHttpFixture _host = new([new DeadJobsHttpFeature()]);
    private long _foreignJobId;
    private long _tenantJobId;

    public async Task InitializeAsync()
    {
        await _host.StartAsync();
        _tenantJobId = await SeedDeadJobAsync(_host.TenantId, "seeded owner error");

        var otherTenantId = Guid.NewGuid();
        await _host.ExecuteOwnerAsync(
            """
            insert into tenants (id, slug, name, default_language)
            values (@id, @slug, 'Other tenant', 'en')
            """,
            new { id = otherTenantId, slug = $"dead-jobs-{otherTenantId:N}" });
        _foreignJobId = await SeedDeadJobAsync(otherTenantId, "seeded other error");
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    [SkippableFact]
    public async Task Owner_sees_a_seeded_dead_tenant_job_through_the_admin_endpoint()
    {
        var jobs = await ListAsync();
        var job = Assert.Single(jobs);

        Assert.Equal(
            (_tenantJobId, "guest-purge", 3, "seeded owner error", "dead", true),
            (job.Id, job.Kind, job.Attempts, job.LastError, job.Status, job.OccurredAt > DateTimeOffset.MinValue));
    }

    [SkippableFact]
    public async Task Owner_does_not_receive_a_plausible_dead_job_from_another_tenant()
    {
        var jobs = await ListAsync();

        Assert.DoesNotContain(jobs, job => job.Id == _foreignJobId);
    }

    [SkippableFact]
    public async Task Owner_cannot_rerun_a_dead_job_from_another_tenant()
    {
        using var response = await _host.Owner.PostAsync($"{DeadJobsPath}/{_foreignJobId}/rerun", content: null);
        var statuses = await _host.QueryOwnerAsync<string>(
            "select status from jobs where id = @id",
            new { id = _foreignJobId });

        Assert.Equal((HttpStatusCode.NotFound, "dead"), (response.StatusCode, Assert.Single(statuses)));
    }

    [SkippableFact]
    public async Task Rerunning_a_dead_job_returns_it_to_the_ready_queue()
    {
        using var response = await _host.Owner.PostAsync($"{DeadJobsPath}/{_tenantJobId}/rerun", content: null);
        var job = await response.Content.ReadFromJsonAsync<DeadJobResponse>();

        Assert.Equal((HttpStatusCode.OK, _tenantJobId, "ready", 0), (response.StatusCode, job!.Id, job.Status, job.Attempts));
    }

    [SkippableFact]
    public async Task Production_worker_finishes_a_rerun_job_and_its_status_is_visible_to_the_owner()
    {
        using var rerunResponse = await _host.Owner.PostAsync($"{DeadJobsPath}/{_tenantJobId}/rerun", content: null);
        rerunResponse.EnsureSuccessStatusCode();

        var job = await WaitForDoneAsync(_tenantJobId);

        Assert.Equal(("done", 1), (job.Status, job.Attempts));
    }

    [SkippableFact]
    public async Task A_second_rerun_after_completion_does_not_schedule_duplicate_work()
    {
        using var rerunResponse = await _host.Owner.PostAsync($"{DeadJobsPath}/{_tenantJobId}/rerun", content: null);
        rerunResponse.EnsureSuccessStatusCode();
        var completed = await WaitForDoneAsync(_tenantJobId);
        var jobBeforeSecondRequest = await GetJobStateAsync(_tenantJobId);

        using var secondResponse = await _host.Owner.PostAsync($"{DeadJobsPath}/{_tenantJobId}/rerun", content: null);
        var jobAfterSecondRequest = await GetJobStateAsync(_tenantJobId);

        Assert.Equal((HttpStatusCode.NotFound, "done", 1), (secondResponse.StatusCode, completed.Status, completed.Attempts));
        Assert.Equal(jobBeforeSecondRequest, jobAfterSecondRequest);
    }

    [SkippableFact]
    public async Task Dead_job_routes_admit_an_owner_and_deny_a_moderator()
    {
        await RoleMatrix.ForOwnerOnlyMutation(_host).AssertAsync(client => client.GetAsync(DeadJobsPath));
        await RoleMatrix.ForOwnerOnlyMutation(_host).AssertAsync(async client =>
        {
            var jobId = await SeedDeadJobAsync(_host.TenantId, "role matrix error");
            return await client.PostAsync($"{DeadJobsPath}/{jobId}/rerun", content: null);
        });
    }

    private async Task<IReadOnlyList<DeadJobResponse>> ListAsync()
    {
        using var response = await _host.Owner.GetAsync(DeadJobsPath);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<DeadJobResponse>>()
            ?? throw new InvalidOperationException("Dead-jobs endpoint returned no body.");
    }

    private async Task<DeadJobState> GetJobStateAsync(long jobId)
    {
        var jobs = await _host.QueryOwnerAsync<DeadJobState>(
            "select id, status, attempts from jobs where tenant_id = @tenantId and id = @jobId",
            new { tenantId = _host.TenantId, jobId });

        return Assert.Single(jobs);
    }

    private async Task<DeadJobResponse> WaitForDoneAsync(long jobId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await _host.Owner.GetAsync($"{DeadJobsPath}/{jobId}");
            response.EnsureSuccessStatusCode();
            var job = await response.Content.ReadFromJsonAsync<DeadJobResponse>()
                ?? throw new InvalidOperationException("Dead-job status endpoint returned no body.");
            if (job.Status == "done")
            {
                return job;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException("The durable jobs worker did not finish the rerun job.");
    }

    private async Task<long> SeedDeadJobAsync(Guid tenantId, string lastError)
    {
        var ids = await _host.QueryOwnerAsync<long>(
            """
            insert into jobs (tenant_id, owner, kind, payload, status, attempts, max_attempts, last_error, finished_at)
            values (@tenantId, 'core', 'guest-purge', '{}'::jsonb, 'dead', 3, 3, @lastError, now())
            returning id
            """,
            new { tenantId, lastError });
        return Assert.Single(ids);
    }

    private sealed record DeadJobResponse(
        long Id,
        string Kind,
        int Attempts,
        string? LastError,
        DateTimeOffset? OccurredAt,
        string Status);

    private sealed record DeadJobState(long Id, string Status, int Attempts);

    private sealed class DeadJobsHttpFeature : IFeature
    {
        public string Id => "dead-jobs-http";

        public FeatureSettings Settings => FeatureSettings.Create<DeadJobsHttpSettings>(
            new DeadJobsHttpSettings(),
            _ => []);

        public void Configure(IFeatureBuilder feature)
        {
        }
    }

    private sealed record DeadJobsHttpSettings;
}
