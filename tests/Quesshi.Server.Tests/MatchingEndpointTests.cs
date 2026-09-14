using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;
using Quesshi.Server.Api;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

[Collection(nameof(ClusterCollection))]
public sealed class MatchingEndpointTests(ClusterFixture fixture)
{
    [Fact]
    public async Task Create_join_read_and_answer_redacts_pending_opponent_answers()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var owner = Player.Register($"owner-{prefix}", $"{prefix}@example.com", "Owner", Language.En, Shared.Clock.Now);
        var other = Player.Register($"other-{prefix}", $"other-{prefix}@example.com", "Other", Language.En, Shared.Clock.Now);
        await Shared.Players.UpsertAsync(owner);
        await Shared.Players.UpsertAsync(other);

        await using var host = new MatchingApiTestHost(fixture.Cluster);
        using var ownerClient = Authenticated(host, owner);
        using var otherClient = Authenticated(host, other);

        var create = await ownerClient.PostAsJsonAsync("/api/matching/lobby",
            new CreateMatchingLobbyDto("en", 10, [category], [], 2, "matching"));
        create.EnsureSuccessStatusCode();
        var lobby = await create.Content.ReadFromJsonAsync<MatchingViewDto>();
        Assert.NotNull(lobby);
        Assert.Equal(owner.AvatarSeed, lobby!.Participants.Single(p => p.Id == owner.Id).AvatarSeed);

        var joined = await otherClient.PostAsync($"/api/matching/join/{lobby!.Code}", null);
        joined.EnsureSuccessStatusCode();
        (await ownerClient.PostAsync($"/api/matching/{lobby.Id}/start", null)).EnsureSuccessStatusCode();

        var started = await ownerClient.GetFromJsonAsync<MatchingViewDto>($"/api/matching/{lobby.Id}");
        Assert.Equal(["participant", "participant", "multiple", "none"],
            started!.CurrentSlot!.Options.Select(option => option.Kind));

        var submitted = await ownerClient.PostAsJsonAsync($"/api/matching/{lobby.Id}/answer",
            new SubmitMatchingAnswerDto(0, "multiple"));
        submitted.EnsureSuccessStatusCode();
        var ownerView = await submitted.Content.ReadFromJsonAsync<MatchingViewDto>();
        Assert.NotNull(ownerView!.OwnAnswer);
        Assert.Empty(ownerView.CurrentSlot!.Answers);
        Assert.Contains(ownerView.CurrentSlot.AnsweredParticipantIds, id => id == owner.Id);
        Assert.Equal(other.AvatarSeed, ownerView.CurrentSlot.Options.Single(o => o.ParticipantId == other.Id).AvatarSeed);
        Assert.Equal("image", ownerView.CurrentSlot.Media!.Kind);
        Assert.Equal("/matching-test.jpg", ownerView.CurrentSlot.Media.Url);
        Assert.Equal("Matching credit", ownerView.CurrentSlot.Media.Attribution);
        Assert.Null(ownerView.Results);

        var otherView = await otherClient.GetFromJsonAsync<MatchingViewDto>($"/api/matching/{lobby.Id}");
        Assert.Empty(otherView!.CurrentSlot!.Answers);
        Assert.Contains(otherView.CurrentSlot.AnsweredParticipantIds, id => id == owner.Id);

        var closed = await otherClient.PostAsJsonAsync($"/api/matching/{lobby.Id}/answer",
            new SubmitMatchingAnswerDto(0, "none"));
        closed.EnsureSuccessStatusCode();
        var closedView = await closed.Content.ReadFromJsonAsync<MatchingViewDto>();
        Assert.Equal(1, closedView!.CurrentSlotIndex);
        Assert.Null(closedView.LastClosedSlot);
        Assert.Empty(closedView.ClosedSlots!);
        Assert.Null(closedView.Results);
    }

    [Fact]
    public async Task Matching_categories_are_publicly_readable_and_lobby_preserves_selection()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var owner = Player.Register($"owner-{prefix}", $"{prefix}@example.com", "Owner", Language.En, Shared.Clock.Now);
        await Shared.Players.UpsertAsync(owner);

        await using var host = new MatchingApiTestHost(fixture.Cluster);
        using var client = Authenticated(host, owner);
        var create = await client.PostAsJsonAsync("/api/matching/lobby",
            new CreateMatchingLobbyDto("en", 10, [category], [], 2, "matching"));
        var lobby = await create.Content.ReadFromJsonAsync<MatchingViewDto>();

        Assert.Equal([category], lobby!.CategoryIds);
        var categories = await client.GetFromJsonAsync<List<MatchingCategoryDto>>(
            "/api/matching/categories?lang=en");
        Assert.Contains(categories!, item => item.Id == category && item.IsActive);

        var update = await client.PutAsJsonAsync($"/api/matching/{lobby.Id}/settings",
            new UpdateMatchingSettingsDto("en", 10, [category], [], 2, "matching"));
        update.EnsureSuccessStatusCode();
        var updated = await client.GetFromJsonAsync<MatchingViewDto>($"/api/matching/{lobby.Id}");
        Assert.Equal([category], updated!.CategoryIds);
    }

    [Fact]
    public async Task Matching_hub_refuses_an_unseated_lobby_reader()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var owner = Player.Register($"owner-{prefix}", $"{prefix}@example.com", "Owner", Language.En, Shared.Clock.Now);
        var stranger = Player.Register($"stranger-{prefix}", $"stranger-{prefix}@example.com", "Stranger", Language.En, Shared.Clock.Now);
        await Shared.Players.UpsertAsync(owner);
        await Shared.Players.UpsertAsync(stranger);

        await using var host = new MatchingApiTestHost(fixture.Cluster);
        using var client = Authenticated(host, owner);
        var create = await client.PostAsJsonAsync("/api/matching/lobby",
            new CreateMatchingLobbyDto("en", 10, [category], [], 2, "matching"));
        var lobby = await create.Content.ReadFromJsonAsync<MatchingViewDto>();

        await using var connection = host.NewHubConnection(host.TokenIssuer.Issue(stranger));
        await connection.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("JoinAsyncLobby", lobby!.Id));

        await using var seated = host.NewHubConnection(host.TokenIssuer.Issue(owner));
        await seated.StartAsync();
        await seated.InvokeAsync("JoinAsyncLobby", lobby!.Id);
    }

    [Fact]
    public async Task In_progress_match_exposes_no_results_and_a_stranger_cannot_read_it()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var owner = Player.Register($"owner-{prefix}", $"{prefix}@example.com", "Owner", Language.En, Shared.Clock.Now);
        var other = Player.Register($"other-{prefix}", $"other-{prefix}@example.com", "Other", Language.En, Shared.Clock.Now);
        var stranger = Player.Register($"stranger-{prefix}", $"stranger-{prefix}@example.com", "Stranger", Language.En, Shared.Clock.Now);
        await Shared.Players.UpsertAsync(owner);
        await Shared.Players.UpsertAsync(other);
        await Shared.Players.UpsertAsync(stranger);

        await using var host = new MatchingApiTestHost(fixture.Cluster);
        using var ownerClient = Authenticated(host, owner);
        using var otherClient = Authenticated(host, other);
        using var strangerClient = Authenticated(host, stranger);
        var create = await ownerClient.PostAsJsonAsync("/api/matching/lobby",
            new CreateMatchingLobbyDto("en", 10, [category], [], 2, "matching"));
        var lobby = await create.Content.ReadFromJsonAsync<MatchingViewDto>();
        Assert.NotNull(lobby);
        await otherClient.PostAsync($"/api/matching/join/{lobby!.Code}", null);
        await ownerClient.PostAsync($"/api/matching/{lobby.Id}/start", null);

        var submitted = await ownerClient.PostAsJsonAsync($"/api/matching/{lobby.Id}/answer",
            new SubmitMatchingAnswerDto(0, "participant", other.Id));
        submitted.EnsureSuccessStatusCode();
        var view = await submitted.Content.ReadFromJsonAsync<MatchingViewDto>();

        Assert.Null(view!.Results);
        Assert.Equal(System.Net.HttpStatusCode.NotFound,
            (await strangerClient.GetAsync($"/api/matching/{lobby.Id}")).StatusCode);
    }

    [Fact]
    public async Task Completed_match_exposes_pair_stats_only_to_participants()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var owner = Player.Register($"owner-{prefix}", $"{prefix}@example.com", "Owner", Language.En, Shared.Clock.Now);
        var other = Player.Register($"other-{prefix}", $"other-{prefix}@example.com", "Other", Language.En, Shared.Clock.Now);
        var stranger = Player.Register($"stranger-{prefix}", $"stranger-{prefix}@example.com", "Stranger", Language.En, Shared.Clock.Now);
        await Shared.Players.UpsertAsync(owner);
        await Shared.Players.UpsertAsync(other);
        await Shared.Players.UpsertAsync(stranger);

        await using var host = new MatchingApiTestHost(fixture.Cluster);
        using var ownerClient = Authenticated(host, owner);
        using var otherClient = Authenticated(host, other);
        using var strangerClient = Authenticated(host, stranger);
        var create = await ownerClient.PostAsJsonAsync("/api/matching/lobby",
            new CreateMatchingLobbyDto("en", 10, [category], [], 2, "matching"));
        var lobby = await create.Content.ReadFromJsonAsync<MatchingViewDto>();
        Assert.NotNull(lobby);
        await otherClient.PostAsync($"/api/matching/join/{lobby!.Code}", null);
        await ownerClient.PostAsync($"/api/matching/{lobby.Id}/start", null);

        MatchingViewDto? final = null;
        for (var slot = 0; slot < 10; slot++)
        {
            var ownerAnswer = await ownerClient.PostAsJsonAsync($"/api/matching/{lobby.Id}/answer",
                new SubmitMatchingAnswerDto(slot, "participant", other.Id));
            ownerAnswer.EnsureSuccessStatusCode();
            var closed = await otherClient.PostAsJsonAsync($"/api/matching/{lobby.Id}/answer",
                new SubmitMatchingAnswerDto(slot, "participant", other.Id));
            closed.EnsureSuccessStatusCode();
            final = await closed.Content.ReadFromJsonAsync<MatchingViewDto>();
        }

        Assert.Equal("resolved", final!.State);
        Assert.NotNull(final.Results);
        Assert.NotNull(final.Results!.PairStats);
        var pair = Assert.Single(final.Results.PairStats!);
        Assert.Equal(10, pair.Same);
        Assert.Equal(0, pair.Different);
        Assert.Equal(100, pair.AgreementPercent);
        Assert.Equal(10, final.ClosedSlots!.Count);

        Assert.Equal(System.Net.HttpStatusCode.NotFound,
            (await strangerClient.GetAsync($"/api/matching/{lobby.Id}")).StatusCode);
    }

    [Fact]
    public async Task No_contest_returns_every_closed_slot_without_overall_statistics()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var owner = Player.Register($"owner-{prefix}", $"owner-{prefix}@example.com", "Owner", Language.En, Shared.Clock.Now);
        var other = Player.Register($"other-{prefix}", $"other-{prefix}@example.com", "Other", Language.En, Shared.Clock.Now);
        var departed = Player.Register($"departed-{prefix}", $"departed-{prefix}@example.com", "Departed", Language.En, Shared.Clock.Now);
        await Shared.Players.UpsertAsync(owner);
        await Shared.Players.UpsertAsync(other);
        await Shared.Players.UpsertAsync(departed);

        await using var host = new MatchingApiTestHost(fixture.Cluster);
        using var ownerClient = Authenticated(host, owner);
        using var otherClient = Authenticated(host, other);
        using var departedClient = Authenticated(host, departed);
        var create = await ownerClient.PostAsJsonAsync("/api/matching/lobby",
            new CreateMatchingLobbyDto("en", 10, [category], [], 3, "matching"));
        var lobby = await create.Content.ReadFromJsonAsync<MatchingViewDto>();
        Assert.NotNull(lobby);
        (await otherClient.PostAsync($"/api/matching/join/{lobby!.Code}", null)).EnsureSuccessStatusCode();
        (await departedClient.PostAsync($"/api/matching/join/{lobby.Code}", null)).EnsureSuccessStatusCode();
        (await ownerClient.PostAsync($"/api/matching/{lobby.Id}/start", null)).EnsureSuccessStatusCode();

        (await ownerClient.PostAsJsonAsync($"/api/matching/{lobby.Id}/answer",
            new SubmitMatchingAnswerDto(0, "none"))).EnsureSuccessStatusCode();
        (await otherClient.PostAsJsonAsync($"/api/matching/{lobby.Id}/answer",
            new SubmitMatchingAnswerDto(0, "none"))).EnsureSuccessStatusCode();
        (await departedClient.PostAsync($"/api/matching/{lobby.Id}/leave", null)).EnsureSuccessStatusCode();
        (await ownerClient.PostAsync($"/api/matching/{lobby.Id}/leave", null)).EnsureSuccessStatusCode();

        var response = await ownerClient.GetAsync($"/api/matching/{lobby.Id}");
        response.EnsureSuccessStatusCode();
        var view = await response.Content.ReadFromJsonAsync<MatchingViewDto>();
        Assert.Equal("nocontest", view!.State);
        var closedSlots = Assert.Single(view.ClosedSlots!);
        Assert.Equal(0, closedSlots.Slot);
        Assert.Equal(2, closedSlots.Answers.Count);
        Assert.NotNull(view.Results);
        var closed = Assert.IsType<MatchingSlotResultDto>(view.Results!.Slots[0]);
        Assert.Equal([0, 0, 0, 0, 2], closed.Counts);
        Assert.True(closed.AllAgreed);
        Assert.Null(view.Results.Slots[1]);
        Assert.Null(view.Results.PairStats);
        Assert.Null(view.Results.AllAgreedCount);
    }

    [Fact]
    public async Task Guest_can_join_read_and_answer_but_cannot_start()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var owner = Player.Register($"owner-{prefix}", $"{prefix}@example.com", "Owner", Language.En, Shared.Clock.Now);
        var guest = Player.Guest($"guest-{prefix}", "Guest", Language.En, Shared.Clock.Now);
        await Shared.Players.UpsertAsync(owner);
        await Shared.Players.UpsertAsync(guest);

        await using var host = new MatchingApiTestHost(fixture.Cluster);
        using var ownerClient = Authenticated(host, owner);
        using var guestClient = Authenticated(host, guest);
        var response = await ownerClient.PostAsJsonAsync("/api/matching/lobby",
            new CreateMatchingLobbyDto("en", 10, [category], [], 2, "matching"));
        var lobby = await response.Content.ReadFromJsonAsync<MatchingViewDto>();
        Assert.NotNull(lobby);

        Assert.Equal(System.Net.HttpStatusCode.OK,
            (await guestClient.PostAsync($"/api/matching/join/{lobby!.Code}", null)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden,
            (await guestClient.PostAsync($"/api/matching/{lobby.Id}/start", null)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK,
            (await ownerClient.PostAsync($"/api/matching/{lobby.Id}/start", null)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK,
            (await guestClient.PostAsJsonAsync($"/api/matching/{lobby.Id}/answer",
                new SubmitMatchingAnswerDto(0, "na"))).StatusCode);
    }

    [Fact]
    public async Task Guest_invite_can_join_matching_without_orphaning_the_guest()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var owner = Player.Register($"owner-{prefix}", $"{prefix}@example.com", "Owner", Language.En, Shared.Clock.Now);
        await Shared.Players.UpsertAsync(owner);
        await using var host = new MatchingApiTestHost(fixture.Cluster);
        using var ownerClient = Authenticated(host, owner);

        var create = await ownerClient.PostAsJsonAsync("/api/matching/lobby",
            new CreateMatchingLobbyDto("en", 10, [category], [], 2, "matching"));
        var lobby = await create.Content.ReadFromJsonAsync<MatchingViewDto>();
        Assert.NotNull(lobby);

        var result = await AuthEndpoints.GuestJoinAsync(lobby!.Code, new GuestJoinDto("Invite Guest"),
            Shared.Archive, Shared.Players, fixture.Cluster.GrainFactory, host.TokenIssuer,
            new FakeIdFactory(700_000), Shared.Clock);

        Assert.Equal(200, CrossTypeCodeTests.StatusOf(result));
        var guestResult = (GuestResultDto)CrossTypeCodeTests.ValueOf(result);
        Assert.True(guestResult.Me.IsGuest);
        Assert.Equal(lobby.Id, guestResult.Match.Id);
        Assert.Contains(Shared.Players.Items, p => p.Id == guestResult.Me.Id);
        Assert.NotNull(await fixture.Cluster.GrainFactory.GetGrain<IMatchingMatchGrain>(lobby.Id)
            .GetAsync(guestResult.Me.Id));
    }

    [Fact]
    public async Task Matching_creation_retries_a_code_index_race_without_leaving_state()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var owner = Player.Register($"owner-{prefix}", $"{prefix}@example.com", "Owner", Language.En, Shared.Clock.Now);
        await Shared.Players.UpsertAsync(owner);
        Shared.Archive.CollisionWritesRemaining = 1;
        try
        {
            var result = await MatchingEndpoints.CreateAsync(
                new CreateMatchingLobbyDto("en", 10, [category], [], 2, "matching"), owner.Id,
                fixture.Cluster.GrainFactory, new FakeIdFactory(800_000), Shared.Archive, Shared.Players,
                Shared.MatchingCategories);

            Assert.Equal(200, CrossTypeCodeTests.StatusOf(result));
            var view = (MatchingViewDto)CrossTypeCodeTests.ValueOf(result);
            Assert.Null(await fixture.Cluster.GrainFactory.GetGrain<IMatchingMatchGrain>("id-800002")
                .GetAsync(owner.Id));
            Assert.Equal(GameMode.Matching, (await Shared.Archive.ByCodeAsync(view.Code))!.Mode);
        }
        finally
        {
            Shared.Archive.CollisionWritesRemaining = 0;
        }
    }

    [Fact]
    public async Task Matching_archive_list_resolves_current_participant_names()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var owner = Player.Register($"owner-{prefix}", $"{prefix}@example.com", "Archive Owner", Language.En, Shared.Clock.Now);
        var other = Player.Register($"other-{prefix}", $"other-{prefix}@example.com", "Archive Other", Language.En, Shared.Clock.Now);
        await Shared.Players.UpsertAsync(owner);
        await Shared.Players.UpsertAsync(other);
        var row = new ArchivedMatch($"matching-{prefix}", $"N{prefix[..5]}", Language.En, owner.Id, other.Id,
            null, false, FakeArchive.TestResults(owner.Id, other.Id, 0, 0), MatchState.InProgress,
            Shared.Clock.Now, null, ["q"], false, GameMode.Matching);
        await Shared.Archive.SaveAsync(row);

        var rows = await GameEndpoints.ListMatchesAsync(owner.Id, false, null, Shared.Archive,
            Shared.Players, fixture.Cluster.GrainFactory);
        var matching = Assert.Single(rows, r => r.Id == row.Id);
        Assert.Equal("Archive Owner", matching.Me.DisplayName);
        Assert.Equal("Archive Other", matching.Opponent!.DisplayName);
        Assert.Null(matching.Me.Score);
        Assert.Equal("matching", matching.Mode);
    }

    [Fact]
    public async Task Stable_answer_errors_are_returned_by_the_http_boundary()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var owner = Player.Register($"owner-{prefix}", $"{prefix}@example.com", "Owner", Language.En, Shared.Clock.Now);
        var other = Player.Register($"other-{prefix}", $"other-{prefix}@example.com", "Other", Language.En, Shared.Clock.Now);
        await Shared.Players.UpsertAsync(owner);
        await Shared.Players.UpsertAsync(other);
        await using var host = new MatchingApiTestHost(fixture.Cluster);
        using var ownerClient = Authenticated(host, owner);
        await ownerClient.PostAsJsonAsync("/api/matching/lobby", new CreateMatchingLobbyDto("en", 10, [category], [], 2, "matching"));

        var badKind = await ownerClient.PostAsJsonAsync("/api/matching/not-created/answer",
            new SubmitMatchingAnswerDto(0, "wat"));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, badKind.StatusCode);
        Assert.Equal("bad_answer_kind", (await badKind.Content.ReadFromJsonAsync<ErrorDto>())!.Error);

        var missing = await ownerClient.PostAsJsonAsync("/api/matching/not-created/answer",
            new SubmitMatchingAnswerDto(0, "na"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Every_matching_answer_refusal_has_its_stable_error_code()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var owner = Player.Register($"owner-{prefix}", $"{prefix}@example.com", "Owner", Language.En, Shared.Clock.Now);
        var other = Player.Register($"other-{prefix}", $"other-{prefix}@example.com", "Other", Language.En, Shared.Clock.Now);
        var third = Player.Register($"third-{prefix}", $"third-{prefix}@example.com", "Third", Language.En, Shared.Clock.Now);
        await Shared.Players.UpsertAsync(owner);
        await Shared.Players.UpsertAsync(other);
        await Shared.Players.UpsertAsync(third);

        var id = $"matching-errors-{prefix}";
        var grain = fixture.Cluster.GrainFactory.GetGrain<IMatchingMatchGrain>(id);
        await grain.CreateAsync($"M{prefix[..5]}", owner.Id, (int)Language.En, 10, [category], 2);
        await grain.JoinAsync(other.Id);

        async Task<string> ErrorAsync(string caller, SubmitMatchingAnswerDto answer, string? matchId = null)
        {
            var result = await MatchingEndpoints.AnswerAsync(matchId ?? id, answer, caller,
                fixture.Cluster.GrainFactory, Shared.Players);
            Assert.Equal(400, CrossTypeCodeTests.StatusOf(result));
            return CrossTypeCodeTests.ErrorOf(result);
        }

        Assert.Equal("bad_answer_kind", await ErrorAsync(owner.Id, new SubmitMatchingAnswerDto(0, "wat")));
        Assert.Equal("missing_field", await ErrorAsync(owner.Id, new SubmitMatchingAnswerDto(0, "participant")));
        Assert.Equal("contradictory_fields", await ErrorAsync(owner.Id,
            new SubmitMatchingAnswerDto(0, "na", ChoiceIndex: 1)));
        var notStarted = fixture.Cluster.GrainFactory.GetGrain<IMatchingMatchGrain>($"matching-not-started-{prefix}");
        await notStarted.CreateAsync($"N{prefix[..5]}", owner.Id, (int)Language.En, 10, [category], 2);
        Assert.Equal("match_not_started", await ErrorAsync(owner.Id, new SubmitMatchingAnswerDto(0, "na"),
            $"matching-not-started-{prefix}"));

        Assert.True(await grain.StartAsync(owner.Id));
        Assert.Equal("stale_slot", await ErrorAsync(owner.Id, new SubmitMatchingAnswerDto(1, "na")));
        Assert.Equal("unknown_participant", await ErrorAsync(owner.Id,
            new SubmitMatchingAnswerDto(0, "participant", ParticipantId: "nobody")));
        Assert.Equal("wrong_answer_kind", await ErrorAsync(owner.Id,
            new SubmitMatchingAnswerDto(0, "choice", ChoiceIndex: 0)));
        Assert.Equal("not_a_participant", await ErrorAsync("outsider", new SubmitMatchingAnswerDto(0, "na")));

        await grain.AnswerAsync(owner.Id, 0, (int)MatchingAnswerKind.NoParticipant, null, null);
        await grain.AnswerAsync(other.Id, 0, (int)MatchingAnswerKind.NoParticipant, null, null);
        Assert.Equal("answers_locked", await ErrorAsync(owner.Id, new SubmitMatchingAnswerDto(0, "na")));

        var leftId = $"matching-left-{prefix}";
        var leftGrain = fixture.Cluster.GrainFactory.GetGrain<IMatchingMatchGrain>(leftId);
        await leftGrain.CreateAsync($"L{prefix[..5]}", owner.Id, (int)Language.En, 10, [category], 3);
        await leftGrain.JoinAsync(other.Id);
        await leftGrain.JoinAsync(third.Id);
        Assert.True(await leftGrain.StartAsync(owner.Id));
        Assert.True(await leftGrain.LeaveAsync(third.Id));
        Assert.Equal("participant_left", await ErrorAsync(third.Id, new SubmitMatchingAnswerDto(0, "na"), leftId));

        var overId = $"matching-over-{prefix}";
        var overGrain = fixture.Cluster.GrainFactory.GetGrain<IMatchingMatchGrain>(overId);
        await overGrain.CreateAsync($"O{prefix[..5]}", owner.Id, (int)Language.En, 10, [category], 2);
        await overGrain.JoinAsync(other.Id);
        Assert.True(await overGrain.StartAsync(owner.Id));
        Assert.True(await overGrain.LeaveAsync(other.Id));
        Assert.Equal("match_over", await ErrorAsync(owner.Id, new SubmitMatchingAnswerDto(0, "na"), overId));

        var fixedCategory = SeedFixed(prefix);
        var fixedId = $"matching-choice-{prefix}";
        var fixedGrain = fixture.Cluster.GrainFactory.GetGrain<IMatchingMatchGrain>(fixedId);
        await fixedGrain.CreateAsync($"F{prefix[..5]}", owner.Id, (int)Language.En, 10, [fixedCategory], 2);
        await fixedGrain.JoinAsync(other.Id);
        Assert.True(await fixedGrain.StartAsync(owner.Id));
        Assert.Equal("bad_choice_index", await ErrorAsync(owner.Id,
            new SubmitMatchingAnswerDto(0, "choice", ChoiceIndex: 99), fixedId));
    }

    [Fact]
    public async Task Matching_settings_and_creation_reject_trivia_levels_and_invalid_lobby_inputs()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var owner = Player.Register($"owner-{prefix}", $"{prefix}@example.com", "Owner", Language.En, Shared.Clock.Now);
        await Shared.Players.UpsertAsync(owner);

        async Task<string> CreateError(CreateMatchingLobbyDto body)
        {
            var result = await MatchingEndpoints.CreateAsync(body, owner.Id, fixture.Cluster.GrainFactory,
                new FakeIdFactory(1_000_000), Shared.Archive, Shared.Players, Shared.MatchingCategories);
            Assert.Equal(400, CrossTypeCodeTests.StatusOf(result));
            return CrossTypeCodeTests.ErrorOf(result);
        }

        Assert.Equal("unknown_category", await CreateError(new CreateMatchingLobbyDto("en", 10, ["trivia-category"], [], 2, "matching")));
        Assert.Equal("levels_not_allowed", await CreateError(new CreateMatchingLobbyDto("en", 10, [category], [1], 2, "matching")));
        Assert.Equal("bad_capacity", await CreateError(new CreateMatchingLobbyDto("en", 10, [category], [], 1, "matching")));
        Assert.Equal("mode_immutable", await CreateError(new CreateMatchingLobbyDto("en", 10, [category], [], 2, "trivia")));

        var id = $"matching-settings-{prefix}";
        var grain = fixture.Cluster.GrainFactory.GetGrain<IMatchingMatchGrain>(id);
        await grain.CreateAsync($"S{prefix[..5]}", owner.Id, (int)Language.En, 10, [category], 2);
        var update = await MatchingEndpoints.UpdateSettingsAsync(id,
            new UpdateMatchingSettingsDto("nl", 20, [category], [], 3, "matching"), owner.Id,
            fixture.Cluster.GrainFactory, Shared.Players, Shared.MatchingCategories);
        Assert.Equal(200, CrossTypeCodeTests.StatusOf(update));
        var view = await grain.GetAsync(owner.Id);
        Assert.Equal(3, view!.Capacity);
        Assert.Equal(20, view.TotalSlots);
    }

    [Fact]
    public async Task Matching_join_and_start_enforce_duplicate_full_started_and_owner_rules()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = Seed(prefix);
        var owner = Player.Register($"owner-{prefix}", $"{prefix}@example.com", "Owner", Language.En, Shared.Clock.Now);
        var other = Player.Register($"other-{prefix}", $"other-{prefix}@example.com", "Other", Language.En, Shared.Clock.Now);
        var third = Player.Register($"third-{prefix}", $"third-{prefix}@example.com", "Third", Language.En, Shared.Clock.Now);
        await Shared.Players.UpsertAsync(owner);
        await Shared.Players.UpsertAsync(other);
        await Shared.Players.UpsertAsync(third);
        await using var host = new MatchingApiTestHost(fixture.Cluster);
        using var ownerClient = Authenticated(host, owner);
        using var otherClient = Authenticated(host, other);
        using var thirdClient = Authenticated(host, third);

        var create = await ownerClient.PostAsJsonAsync("/api/matching/lobby",
            new CreateMatchingLobbyDto("en", 10, [category], [], 2, "matching"));
        var lobby = await create.Content.ReadFromJsonAsync<MatchingViewDto>();
        Assert.NotNull(lobby);
        Assert.Equal(System.Net.HttpStatusCode.OK,
            (await otherClient.PostAsync($"/api/matching/join/{lobby!.Code}", null)).StatusCode);
        var duplicate = await otherClient.PostAsync($"/api/matching/join/{lobby.Code}", null);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Equal("duplicate_join", (await duplicate.Content.ReadFromJsonAsync<ErrorDto>())!.Error);
        var full = await thirdClient.PostAsync($"/api/matching/join/{lobby.Code}", null);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, full.StatusCode);
        Assert.Equal("full_lobby", (await full.Content.ReadFromJsonAsync<ErrorDto>())!.Error);
        var nonOwnerStart = await otherClient.PostAsync($"/api/matching/{lobby.Id}/start", null);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, nonOwnerStart.StatusCode);
        Assert.Equal("cannot_start", (await nonOwnerStart.Content.ReadFromJsonAsync<ErrorDto>())!.Error);
        Assert.Equal(System.Net.HttpStatusCode.OK,
            (await ownerClient.PostAsync($"/api/matching/{lobby.Id}/start", null)).StatusCode);
        var started = await thirdClient.PostAsync($"/api/matching/join/{lobby.Code}", null);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, started.StatusCode);
        Assert.Equal("match_started", (await started.Content.ReadFromJsonAsync<ErrorDto>())!.Error);
    }

    [Fact]
    public async Task Matching_start_returns_503_with_not_enough_questions_message()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var category = $"m-empty-{prefix}";
        Shared.MatchingCategories.Items.Add(new MatchingCategory(category, "خالی", "Empty", "x", "#000"));
        var owner = Player.Register($"owner-{prefix}", $"{prefix}@example.com", "Owner", Language.En, Shared.Clock.Now);
        var other = Player.Register($"other-{prefix}", $"other-{prefix}@example.com", "Other", Language.En, Shared.Clock.Now);
        await Shared.Players.UpsertAsync(owner);
        await Shared.Players.UpsertAsync(other);
        await using var host = new MatchingApiTestHost(fixture.Cluster);
        using var ownerClient = Authenticated(host, owner);
        using var otherClient = Authenticated(host, other);
        var create = await ownerClient.PostAsJsonAsync("/api/matching/lobby",
            new CreateMatchingLobbyDto("en", 10, [category], [], 2, "matching"));
        var lobby = await create.Content.ReadFromJsonAsync<MatchingViewDto>();
        Assert.NotNull(lobby);
        (await otherClient.PostAsync($"/api/matching/join/{lobby!.Code}", null)).EnsureSuccessStatusCode();

        var start = await ownerClient.PostAsync($"/api/matching/{lobby.Id}/start", null);
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, start.StatusCode);
        var problem = await start.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("not_enough_questions", problem.GetProperty("error").GetString());
        Assert.Contains("Not enough", problem.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient Authenticated(MatchingApiTestHost host, Player player)
    {
        var client = host.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", host.TokenIssuer.Issue(player));
        return client;
    }

    private static string Seed(string prefix)
    {
        var categoryId = $"m-{prefix}";
        Shared.MatchingCategories.Items.Add(new MatchingCategory(categoryId, "آزمون", "Test", "x", "#000"));
        for (var i = 0; i < 10; i++)
            Shared.MatchingQuestions.Items.Add(MatchingQuestion.Create($"mq-{prefix}-{i}", Language.En,
                categoryId, $"Prompt {i}", MatchingAnswerSource.Participants, null, Shared.Clock.Now,
                new MediaRef(MediaKind.Image, "/matching-test.jpg", "Matching credit"),
                status: QuestionStatus.Approved));
        return categoryId;
    }

    private static string SeedFixed(string prefix)
    {
        var categoryId = $"m-fixed-{prefix}";
        Shared.MatchingCategories.Items.Add(new MatchingCategory(categoryId, "اختیار", "Fixed", "x", "#000"));
        for (var i = 0; i < 10; i++)
            Shared.MatchingQuestions.Items.Add(MatchingQuestion.Create($"mq-fixed-{prefix}-{i}", Language.En,
                categoryId, $"Fixed prompt {i}", MatchingAnswerSource.Fixed, ["A", "B"], Shared.Clock.Now,
                status: QuestionStatus.Approved));
        return categoryId;
    }

    private sealed record ErrorDto(string Error);
}
