using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Quesshi.Application;
using Quesshi.Engine.Contracts;
using Quesshi.Formats.AsyncDuel;
using Quesshi.Formats.Poll;
using Quesshi.Grains.Abstractions;
using Quesshi.Plugin.Api;

namespace Quesshi.Server.Tests;

/// <summary>Lobby join, configure, start, leave and deadline behavior over the real host and PostgreSQL database.</summary>
public sealed class SessionLobbyDatabaseTests(SessionLobbyDatabaseTests.LobbyHost host) : IClassFixture<SessionLobbyDatabaseTests.LobbyHost>
{
    private const string Shared = "lobby_shared";
    private const string Personal = "lobby_personal";
    private const string NoGuests = "lobby_noguests";

    [SkippableFact]
    public async Task Second_player_joins_by_code_and_repeating_the_join_keeps_one_seat()
    {
        var fixture = host.Fixture;
        var (second, secondId) = await fixture.CreateSecondPlayerClientAsync();
        var lobby = await CreateAsync(Shared);

        var first = await JoinAsync(second, lobby.Code);
        var repeat = await JoinAsync(second, lobby.Code);

        Assert.Equal((HttpStatusCode.OK, "joined", HttpStatusCode.OK, "joined", 1), (first.Status, first.Code, repeat.Status, repeat.Code, await ActiveSeatsAsync(lobby.Id, secondId)));
    }

    [SkippableFact]
    public async Task An_allowed_guest_joins_by_code()
    {
        var lobby = await CreateAsync(Shared);

        var joined = await JoinAsync(host.Fixture.Guest, lobby.Code);

        Assert.Equal((HttpStatusCode.OK, "joined"), (joined.Status, joined.Code));
    }

    [SkippableFact]
    public async Task A_guest_is_refused_when_the_format_disallows_guests()
    {
        var lobby = await CreateAsync(NoGuests);

        var refused = await JoinAsync(host.Fixture.Guest, lobby.Code);

        Assert.Equal((HttpStatusCode.Forbidden, "guest_not_allowed"), (refused.Status, refused.Code));
    }

    [SkippableFact]
    public async Task The_grain_accepts_a_join_by_tenant_scoped_session_id()
    {
        var (_, secondId) = await host.Fixture.CreateSecondPlayerClientAsync();
        var lobby = await CreateAsync(Shared);

        var joined = await Grain(lobby.Id).ExecuteLobbyAsync(new(secondId.ToString(), "join"));
        var repeat = await Grain(lobby.Id).ExecuteLobbyAsync(new(secondId.ToString(), "join"));

        Assert.Equal(("joined", "joined", 1), (joined.Code, repeat.Code, await ActiveSeatsAsync(lobby.Id, secondId)));
    }

    [SkippableFact]
    public async Task Joining_a_full_lobby_is_refused()
    {
        var (second, _) = await host.Fixture.CreateSecondPlayerClientAsync();
        var lobby = await CreateAsync(Shared, capacity: 2);
        _ = await JoinAsync(second, lobby.Code);

        var refused = await JoinAsync(host.Fixture.Guest, lobby.Code);

        Assert.Equal((HttpStatusCode.Conflict, "session_full"), (refused.Status, refused.Code));
    }

    [SkippableFact]
    public async Task Joining_a_running_session_whose_format_disallows_late_join_is_refused()
    {
        var (second, _) = await host.Fixture.CreateSecondPlayerClientAsync();
        var lobby = await CreateAsync(Shared, capacity: 3);
        _ = await JoinAsync(second, lobby.Code);
        var started = await host.Fixture.Player.PostAsync($"/api/sessions/{lobby.Id}/start", null);

        var refused = await JoinAsync(host.Fixture.Guest, lobby.Code);

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.Conflict, "late_join_not_allowed"), (started.StatusCode, refused.Status, refused.Code));
    }

    [SkippableFact]
    public async Task Joining_an_expired_lobby_is_refused()
    {
        var lobby = await CreateAsync(Shared);
        await host.Fixture.ExecuteOwnerAsync(
            "update sessions set due_at = now() - interval '1 minute', retry_at = now() + interval '5 minutes' where id = @id",
            new { id = lobby.Id });

        var refused = await JoinAsync(host.Fixture.Guest, lobby.Code);

        Assert.Equal((HttpStatusCode.Conflict, "expired"), (refused.Status, refused.Code));
    }

    [SkippableFact]
    public async Task Joining_a_cancelled_session_is_refused()
    {
        var lobby = await CreateAsync(Shared);
        _ = await Grain(lobby.Id).ExecuteLobbyAsync(new(host.Fixture.PlayerId.ToString(), "cancel"));

        var refused = await JoinAsync(host.Fixture.Guest, lobby.Code);

        Assert.Equal((HttpStatusCode.Conflict, "cancelled"), (refused.Status, refused.Code));
    }

    [SkippableFact]
    public async Task Another_tenants_code_is_indistinguishable_from_a_missing_code()
    {
        var lobby = await CreateAsync(Shared);
        var other = await host.Fixture.CreateOtherTenantPlayerClientAsync();

        var crossTenant = await JoinAsync(other, lobby.Code);
        var missing = await JoinAsync(other, "NOSUCHCODE");

        Assert.Equal((HttpStatusCode.NotFound, "not_found"), (crossTenant.Status, crossTenant.Code));
        Assert.Equal((missing.Status, missing.Code), (crossTenant.Status, crossTenant.Code));
    }

    [SkippableFact]
    public async Task An_open_session_accepts_a_second_player_after_its_creator_answered_until_capacity()
    {
        var (second, secondId) = await host.Fixture.CreateSecondPlayerClientAsync();
        var (_, thirdId) = await host.Fixture.CreateSecondPlayerClientAsync();
        var id = Guid.NewGuid();
        var creator = await CreateOpenAsync(id, Shared, capacity: 2);
        var answered = await Grain(id).ExecuteAsync(new(creator.ToString(), "answer", "0", 0, 0, 0, null, "{\"index\":0}", DateTimeOffset.UtcNow));

        var joined = await Grain(id).ExecuteLobbyAsync(new(secondId.ToString(), "join"));
        var full = await Grain(id).ExecuteLobbyAsync(new(thirdId.ToString(), "join"));

        Assert.Equal(("ok", "joined", "session_full"), (answered.Code, joined.Code, full.Code));
    }

    [SkippableFact]
    public async Task Only_the_owner_may_change_settings_start_or_cancel()
    {
        var (second, secondId) = await host.Fixture.CreateSecondPlayerClientAsync();
        var lobby = await CreateAsync(Shared, capacity: 3);
        _ = await JoinAsync(second, lobby.Code);

        var settings = await second.PutAsJsonAsync($"/api/sessions/{lobby.Id}/settings", new { capacity = 2 });
        var start = await second.PostAsync($"/api/sessions/{lobby.Id}/start", null);
        var cancel = await Grain(lobby.Id).ExecuteLobbyAsync(new(secondId.ToString(), "cancel"));

        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, "not_owner"), (settings.StatusCode, start.StatusCode, cancel.Code));
    }

    [SkippableFact]
    public async Task Owner_settings_change_applies_the_formats_rules_check()
    {
        var lobby = await CreateAsync(Shared, capacity: 3);

        var invalid = await host.Fixture.Player.PutAsJsonAsync($"/api/sessions/{lobby.Id}/settings", new { rules = "not-an-object" });
        var valid = await host.Fixture.Player.PutAsJsonAsync($"/api/sessions/{lobby.Id}/settings", new { rules = new { rounds = 2 } });

        Assert.Equal((HttpStatusCode.BadRequest, "invalid_rules", HttpStatusCode.OK), (invalid.StatusCode, (await ErrorAsync(invalid)), valid.StatusCode));
    }

    [SkippableFact]
    public async Task A_source_that_denies_the_owner_rejects_a_settings_change()
    {
        var lobby = await CreateAsync(Shared, capacity: 3);
        await host.Fixture.SetCapabilityAsync("source", "bank", false);
        try
        {
            var denied = await host.Fixture.Player.PutAsJsonAsync($"/api/sessions/{lobby.Id}/settings", new { content = new { source = "bank", parameters = new { }, count = 20 } });

            Assert.Equal((HttpStatusCode.ServiceUnavailable, "source_unavailable"), (denied.StatusCode, await ErrorAsync(denied)));
        }
        finally { await host.Fixture.SetCapabilityAsync("source", "bank", true); }
    }

    [SkippableFact]
    public async Task Capacity_cannot_drop_below_occupied_seats_or_exceed_the_tenant_and_format_limits()
    {
        var (second, _) = await host.Fixture.CreateSecondPlayerClientAsync();
        var lobby = await CreateAsync(Shared, capacity: 4);
        _ = await JoinAsync(second, lobby.Code);

        var belowSeats = await Capacity(lobby.Id, 1);
        var atSeats = await Capacity(lobby.Id, 2);
        var aboveFormat = await Capacity(lobby.Id, 5);
        await host.Fixture.SetCapabilityAsync("switch", "sessions", true);
        await host.Fixture.ExecuteOwnerAsync("update tenant_capabilities set settings = '{\"capacity\":3}'::jsonb where tenant_id = @t and type = 'switch' and key = 'sessions'", new { t = host.Fixture.TenantId });
        try
        {
            var aboveTenant = await Capacity(lobby.Id, 4);
            var atTenant = await Capacity(lobby.Id, 3);
            Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.OK, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.OK),
                (belowSeats, atSeats, aboveFormat, aboveTenant, atTenant));
        }
        finally
        {
            await host.Fixture.ExecuteOwnerAsync("delete from tenant_capabilities where tenant_id = @t and type = 'switch' and key = 'sessions'", new { t = host.Fixture.TenantId });
        }

        async Task<HttpStatusCode> Capacity(Guid id, int capacity) =>
            (await host.Fixture.Player.PutAsJsonAsync($"/api/sessions/{id}/settings", new { capacity })).StatusCode;
    }

    [SkippableFact]
    public async Task Capacity_counts_active_player_roles_only()
    {
        var (second, secondId) = await host.Fixture.CreateSecondPlayerClientAsync();
        var (_, spectatorId) = await host.Fixture.CreateSecondPlayerClientAsync();
        var lobby = await CreateAsync(Shared, capacity: 2);
        await host.Fixture.ExecuteOwnerAsync("""
            insert into session_participants(tenant_id, session_id, player_id, seat, role, phase, slot, step)
            values (@t, @id, @spectator, 5, 'spectator', 'idle', -1, 0)
            """, new { t = host.Fixture.TenantId, id = lobby.Id, spectator = spectatorId });

        var joined = await JoinAsync(second, lobby.Code);
        var full = await JoinAsync(host.Fixture.Guest, lobby.Code);

        Assert.Equal(("joined", "session_full"), (joined.Code, full.Code));
    }

    [SkippableTheory]
    [InlineData(10, true)]
    [InlineData(20, true)]
    [InlineData(30, true)]
    [InlineData(40, true)]
    [InlineData(50, true)]
    [InlineData(100, true)]
    [InlineData(15, false)]
    [InlineData(0, false)]
    public async Task Legacy_question_counts_are_accepted_and_others_rejected(int count, bool accepted)
    {
        var lobby = await CreateAsync("async_duel", capacity: 2);

        var response = await host.Fixture.Player.PutAsJsonAsync($"/api/sessions/{lobby.Id}/settings", new { content = new { source = "bank", parameters = new { }, count } });

        Assert.True(response.StatusCode == (accepted ? HttpStatusCode.OK : HttpStatusCode.BadRequest), await response.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task A_non_owner_leaving_frees_their_seat()
    {
        var (second, secondId) = await host.Fixture.CreateSecondPlayerClientAsync();
        var lobby = await CreateAsync(Shared, capacity: 2);
        _ = await JoinAsync(second, lobby.Code);

        var left = await second.PostAsync($"/api/sessions/{lobby.Id}/leave", null);
        var rejoined = await JoinAsync(host.Fixture.Guest, lobby.Code);

        Assert.Equal((HttpStatusCode.OK, 0, "joined"), (left.StatusCode, await ActiveSeatsAsync(lobby.Id, secondId), rejoined.Code));
    }

    [SkippableFact]
    public async Task The_owner_leaving_ends_the_lobby_for_everyone_with_the_owner_ending()
    {
        var (second, _) = await host.Fixture.CreateSecondPlayerClientAsync();
        var lobby = await CreateAsync(Shared, capacity: 2);
        _ = await JoinAsync(second, lobby.Code);

        var left = await host.Fixture.Player.PostAsync($"/api/sessions/{lobby.Id}/leave", null);
        var after = await second.PostAsync($"/api/sessions/{lobby.Id}/leave", null);
        var ended = await EndedAsync(lobby.Id);

        Assert.Equal((HttpStatusCode.OK, ("cancelled", "owner", (DateTimeOffset?)null, 0), HttpStatusCode.Conflict), (left.StatusCode, ended, after.StatusCode));
    }

    [SkippableFact]
    public async Task Cancelling_ends_the_lobby_and_a_second_command_is_refused()
    {
        var lobby = await CreateAsync(Shared);
        var owner = host.Fixture.PlayerId.ToString();

        var cancelled = await Grain(lobby.Id).ExecuteLobbyAsync(new(owner, "cancel"));
        var again = await Grain(lobby.Id).ExecuteLobbyAsync(new(owner, "cancel"));
        var start = await host.Fixture.Player.PostAsync($"/api/sessions/{lobby.Id}/start", null);
        var ended = await EndedAsync(lobby.Id);

        Assert.Equal(("ended", "cancelled", HttpStatusCode.Conflict, ("cancelled", "owner", (DateTimeOffset?)null, 0)), (cancelled.Code, again.Code, start.StatusCode, ended));
    }

    [SkippableFact]
    public async Task Start_requires_the_formats_minimum_roster()
    {
        var lobby = await CreateAsync(Shared);

        var start = await host.Fixture.Player.PostAsync($"/api/sessions/{lobby.Id}/start", null);

        Assert.Equal((HttpStatusCode.Conflict, "not_enough_players", "lobby"), (start.StatusCode, await ErrorAsync(start), (await StateAsync(lobby.Id)).Status));
    }

    [SkippableFact]
    public async Task Start_draws_the_items_once_and_a_repeated_start_does_not_draw_again()
    {
        var (second, _) = await host.Fixture.CreateSecondPlayerClientAsync();
        var lobby = await CreateAsync(Shared, capacity: 3);
        _ = await JoinAsync(second, lobby.Code);

        var started = await host.Fixture.Player.PostAsync($"/api/sessions/{lobby.Id}/start", null);
        var items = await ItemsAsync(lobby.Id);
        var repeated = await host.Fixture.Player.PostAsync($"/api/sessions/{lobby.Id}/start", null);

        Assert.Equal((HttpStatusCode.OK, 10, HttpStatusCode.Conflict, 10, "running"), (started.StatusCode, items, repeated.StatusCode, await ItemsAsync(lobby.Id), (await StateAsync(lobby.Id)).Status));
    }

    [SkippableFact]
    public async Task Start_with_too_few_questions_leaves_the_lobby_seats_rules_and_deadline_intact()
    {
        var (second, secondId) = await host.Fixture.CreateSecondPlayerClientAsync();
        var lobby = await CreateAsync(Shared, capacity: 3);
        _ = await JoinAsync(second, lobby.Code);
        _ = await host.Fixture.Player.PutAsJsonAsync($"/api/sessions/{lobby.Id}/settings", new { rules = new { rounds = 7 }, content = new { source = "bank", parameters = new { }, count = 100 } });
        await host.Fixture.ExecuteOwnerAsync("update sessions set due_at = now() + interval '3 hours' where id = @id", new { id = lobby.Id });
        var before = await StateAsync(lobby.Id);

        var start = await host.Fixture.Player.PostAsync($"/api/sessions/{lobby.Id}/start", null);

        var after = await StateAsync(lobby.Id);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "not_enough_questions", 0), (start.StatusCode, await ErrorAsync(start), await ItemsAsync(lobby.Id)));
        Assert.Equal((before.Status, before.DueAt, before.Rules), (after.Status, after.DueAt, after.Rules));
        Assert.Equal(1, await ActiveSeatsAsync(lobby.Id, secondId));
    }

    [SkippableFact]
    public async Task Start_with_an_unavailable_source_leaves_the_lobby_intact()
    {
        var (second, _) = await host.Fixture.CreateSecondPlayerClientAsync();
        var lobby = await CreateAsync(Shared, capacity: 3);
        _ = await JoinAsync(second, lobby.Code);
        await host.Fixture.SetCapabilityAsync("source", "bank", false);
        try
        {
            var start = await host.Fixture.Player.PostAsync($"/api/sessions/{lobby.Id}/start", null);

            Assert.Equal((HttpStatusCode.ServiceUnavailable, "source_unavailable", "lobby", 0), (start.StatusCode, await ErrorAsync(start), (await StateAsync(lobby.Id)).Status, await ItemsAsync(lobby.Id)));
        }
        finally { await host.Fixture.SetCapabilityAsync("source", "bank", true); }
    }

    [SkippableFact]
    public async Task A_guest_who_is_still_a_guest_at_shared_start_stays_ineligible_after_upgrading()
    {
        var (guest, guestId) = await host.Fixture.CreateAnotherGuestClientAsync();
        var lobby = await CreateAsync(Shared, capacity: 3);
        _ = await JoinAsync(guest, lobby.Code);
        _ = await host.Fixture.Player.PostAsync($"/api/sessions/{lobby.Id}/start", null);

        await UpgradeAsync(guestId);

        Assert.False(await EligibleAsync(lobby.Id, guestId));
    }

    [SkippableFact]
    public async Task A_guest_who_upgrades_before_shared_start_is_eligible()
    {
        var (guest, guestId) = await host.Fixture.CreateAnotherGuestClientAsync();
        var lobby = await CreateAsync(Shared, capacity: 3);
        _ = await JoinAsync(guest, lobby.Code);
        await UpgradeAsync(guestId);

        _ = await host.Fixture.Player.PostAsync($"/api/sessions/{lobby.Id}/start", null);

        Assert.True(await EligibleAsync(lobby.Id, guestId));
    }

    [SkippableFact]
    public async Task A_late_shared_player_is_eligible_at_the_seat_and_a_late_guest_is_not()
    {
        var (_, lateId) = await host.Fixture.CreateSecondPlayerClientAsync();
        var (_, guestId) = await host.Fixture.CreateAnotherGuestClientAsync();
        var id = Guid.NewGuid();
        _ = await CreateOpenAsync(id, Shared, capacity: 4);

        _ = await Grain(id).ExecuteLobbyAsync(new(lateId.ToString(), "join"));
        _ = await Grain(id).ExecuteLobbyAsync(new(guestId.ToString(), "join"));

        Assert.Equal((true, false), (await EligibleAsync(id, lateId), await EligibleAsync(id, guestId)));
    }

    [SkippableFact]
    public async Task A_personal_guest_is_ineligible_after_the_first_card_even_if_they_upgrade_later()
    {
        var (guest, guestId) = await host.Fixture.CreateAnotherGuestClientAsync();
        var lobby = await CreateAsync(Personal, capacity: 3);
        _ = await JoinAsync(guest, lobby.Code);
        _ = await host.Fixture.Player.PostAsync($"/api/sessions/{lobby.Id}/start", null);

        await UpgradeAsync(guestId);

        Assert.Equal((false, true), (await EligibleAsync(lobby.Id, guestId), await EligibleAsync(lobby.Id, host.Fixture.PlayerId)));
    }

    [SkippableFact]
    public async Task Starting_a_personal_lobby_persists_the_cards_the_format_served_to_each_seat()
    {
        var (second, _) = await host.Fixture.CreateSecondPlayerClientAsync();
        var lobby = await CreateAsync(Personal);
        _ = await JoinAsync(second, lobby.Code);

        var started = await host.Fixture.Player.PostAsync($"/api/sessions/{lobby.Id}/start", null);

        var seat = (await host.Fixture.QueryOwnerAsync<int>(
            "select count(*)::int from session_participants where session_id = @id and player_id = @player and slot = 0 and phase <> 'idle'",
            new { id = lobby.Id, player = host.Fixture.PlayerId })).Single();
        Assert.Equal((HttpStatusCode.OK, 1), (started.StatusCode, seat));
    }

    [SkippableFact]
    public async Task A_code_shared_by_an_ended_session_and_a_live_lobby_joins_the_live_lobby_in_any_case()
    {
        var (second, secondId) = await host.Fixture.CreateSecondPlayerClientAsync();
        var ended = await CreateAsync(Shared);
        _ = await host.Fixture.Player.PostAsync($"/api/sessions/{ended.Id}/leave", null);
        var live = await CreateAsync(Shared);
        await host.Fixture.ExecuteOwnerAsync("update sessions set code = @code where id = @id", new { code = ended.Code, id = live.Id });

        var joined = await JoinAsync(second, ended.Code.ToLowerInvariant());

        Assert.Equal((HttpStatusCode.OK, "joined", 1), (joined.Status, joined.Code, await ActiveSeatsAsync(live.Id, secondId)));
    }

    [SkippableFact]
    public async Task A_personal_guest_who_upgrades_before_the_first_card_is_eligible()
    {
        var (guest, guestId) = await host.Fixture.CreateAnotherGuestClientAsync();
        var lobby = await CreateAsync(Personal, capacity: 3);
        _ = await JoinAsync(guest, lobby.Code);
        await UpgradeAsync(guestId);

        _ = await host.Fixture.Player.PostAsync($"/api/sessions/{lobby.Id}/start", null);

        Assert.True(await EligibleAsync(lobby.Id, guestId));
    }

    [SkippableFact]
    public async Task An_overdue_ordinary_lobby_ends_as_expired_exactly_once_after_a_cold_activation_and_duplicate_delivery()
    {
        var lobby = await CreateAsync(Shared);
        await host.Fixture.ExecuteOwnerAsync("update sessions set due_at = now() - interval '1 minute' where id = @id", new { id = lobby.Id });
        var due = (await StateAsync(lobby.Id)).DueAt!.Value;

        await Grain(lobby.Id).TimeoutAsync(due);
        var first = await EndedAsync(lobby.Id);
        var version = (await StateAsync(lobby.Id)).Version;
        await Grain(lobby.Id).TimeoutAsync(due);

        Assert.Equal(("cancelled", "expired", (DateTimeOffset?)null, 0), first);
        Assert.Equal(version, (await StateAsync(lobby.Id)).Version);
    }

    [SkippableFact]
    public async Task An_async_duel_still_in_its_lobby_at_the_creation_anchor_follows_the_format_forfeit()
    {
        var lobby = await CreateAsync("async_duel", capacity: 2);
        await host.Fixture.ExecuteOwnerAsync("update sessions set due_at = now() - interval '1 minute' where id = @id", new { id = lobby.Id });
        var due = (await StateAsync(lobby.Id)).DueAt!.Value;

        await Grain(lobby.Id).TimeoutAsync(due);
        var state = await StateAsync(lobby.Id);
        await Grain(lobby.Id).TimeoutAsync(due);

        Assert.NotEqual("expired", state.EndReason);
        Assert.Equal(("finished", "forfeit"), (state.Status, state.EndReason));
        Assert.Equal(state.Version, (await StateAsync(lobby.Id)).Version);
    }

    [SkippableFact]
    public async Task Lobby_routes_require_a_signed_in_player_and_ignore_the_legacy_player_header()
    {
        var lobby = await CreateAsync(Shared);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/sessions/join") { Content = JsonContent.Create(new { code = lobby.Code }) };
        request.Headers.Add("X-Quesshi-Test-Player", host.Fixture.PlayerId.ToString());

        var anonymous = await host.Fixture.Anonymous.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [SkippableFact]
    public async Task Invalid_lobby_requests_return_the_documented_error_object()
    {
        var lobby = await CreateAsync(Shared);

        var emptyCode = await host.Fixture.Player.PostAsJsonAsync("/api/sessions/join", new { code = "" });
        var noBody = await host.Fixture.Player.PostAsync("/api/sessions/join", new StringContent("", System.Text.Encoding.UTF8, "application/json"));
        var malformed = await host.Fixture.Player.PostAsync("/api/sessions/join", new StringContent("{", System.Text.Encoding.UTF8, "application/json"));
        var badSettings = await host.Fixture.Player.PutAsJsonAsync($"/api/sessions/{lobby.Id}/settings", new { capacity = "many" });

        foreach (var response in new[] { emptyCode, noBody, malformed, badSettings })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.False(string.IsNullOrEmpty(await ErrorAsync(response)));
        }
    }

    private async Task<(Guid Id, string Code)> CreateAsync(string format, int capacity = 2, int count = 10)
    {
        var id = Guid.NewGuid();
        var created = await host.Fixture.Player.PostAsJsonAsync("/api/sessions", new
        {
            id,
            format,
            content = new { source = "bank", parameters = new { }, count },
            rules = new { },
            capacity,
        });
        var body = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.Created, body);
        using var document = JsonDocument.Parse(body);
        return (id, document.RootElement.GetProperty("code").GetString()!);
    }

    private async Task<Guid> CreateOpenAsync(Guid id, string format, int capacity)
    {
        var tenants = host.Fixture.Services.GetRequiredService<ITenantDb>();
        var sessions = host.Fixture.Services.GetRequiredService<ISessions>();
        var request = new SessionCreateRequest(id, format, new("bank", JsonSerializer.SerializeToElement(new { }), 10), JsonSerializer.SerializeToElement(new { }), capacity, SessionShape.Open);
        var result = await tenants.InTenantAsync(host.Fixture.TenantId, tx => sessions.CreateAsync(tx, host.Fixture.PlayerId, request));
        Assert.Equal(SessionCreateStatus.Created, result.Status);
        return host.Fixture.PlayerId;
    }

    private async Task<(HttpStatusCode Status, string? Code)> JoinAsync(HttpClient client, string code)
    {
        var response = await client.PostAsJsonAsync("/api/sessions/join", new { code });
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var value = root.TryGetProperty("code", out var ok) ? ok.GetString() : root.TryGetProperty("error", out var error) ? error.GetString() : null;
        return (response.StatusCode, value);
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
    }

    private ISessionGrain Grain(Guid id) => host.Fixture.Services.GetRequiredService<IGrainFactory>().GetGrain<ISessionGrain>($"{host.Fixture.TenantId}/{id}");

    private async Task<int> ActiveSeatsAsync(Guid id, Guid player) => (await host.Fixture.QueryOwnerAsync<int>(
        "select count(*)::int from session_participants where session_id = @id and player_id = @player and status = 'active'", new { id, player })).Single();

    private async Task<int> ItemsAsync(Guid id) => (await host.Fixture.QueryOwnerAsync<int>("select count(*)::int from session_items where session_id = @id", new { id })).Single();

    private async Task<bool> EligibleAsync(Guid id, Guid player) => (await host.Fixture.QueryOwnerAsync<bool>(
        "select eligible from session_participants where session_id = @id and player_id = @player", new { id, player })).Single();

    private async Task<(string Status, string? EndReason, DateTimeOffset? DueAt, long Version, string Rules)> StateAsync(Guid id)
    {
        var row = (await host.Fixture.QueryOwnerAsync<StateRow>("select status, end_reason as EndReason, due_at as DueAt, version, rules::text as Rules from sessions where id = @id", new { id })).Single();
        return (row.Status, row.EndReason, row.DueAt, row.Version, row.Rules);
    }

    private async Task<(string Status, string? EndReason, DateTimeOffset? DueAt, int Ranked)> EndedAsync(Guid id)
    {
        var state = await StateAsync(id);
        var ranked = (await host.Fixture.QueryOwnerAsync<int>("select count(*)::int from session_participants where session_id = @id and ranked", new { id })).Single();
        return (state.Status, state.EndReason, state.DueAt, ranked);
    }

    private Task UpgradeAsync(Guid guestId)
    {
        var account = Guid.NewGuid();
        return host.Fixture.ExecuteOwnerAsync("""
            insert into accounts(id, email) values (@account, @email);
            update players set account_id = @account where id = @guestId
            """, new { account, guestId, email = $"upgraded-{account:N}@example.test" });
    }

    private sealed class StateRow
    {
        public string Status { get; init; } = "";
        public string? EndReason { get; init; }
        public DateTimeOffset? DueAt { get; init; }
        public long Version { get; init; }
        public string Rules { get; init; } = "";
    }

    public sealed class LobbyHost : IAsyncLifetime
    {
        public PlatformHttpFixture Fixture { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            if (Environment.GetEnvironmentVariable("QUESSHI_TEST_POSTGRES") is null) return;
            Fixture = new PlatformHttpFixture([new LobbyFeature()])
            {
                Formats = new FormatRegistry([new AsyncDuelFormat(), new PollFormat(), new LobbyFormat(Shared, Pacing.Shared, true), new LobbyFormat(Personal, Pacing.Personal, true), new LobbyFormat(NoGuests, Pacing.Shared, false)]),
            };
            await Fixture.StartAsync();
            foreach (var format in new[] { "async_duel", Shared, Personal, NoGuests }) await Fixture.SetCapabilityAsync("format", format, true);
            await Fixture.SetCapabilityAsync("kind", "choice", true);
            var category = Guid.NewGuid();
            await Fixture.ExecuteOwnerAsync("""
                insert into categories(tenant_id, id, slug, names) values (@t, @category, 'lobby-category', '{"en":"Lobby"}'::jsonb);
                insert into tenant_capabilities(tenant_id, type, key, entitled, enabled, settings)
                values (@t, 'source', 'bank', true, true, jsonb_build_object('CategoriesByFamily', jsonb_build_object('trivia', jsonb_build_array(@category::text))))
                on conflict (tenant_id, type, key) do update set entitled = true, enabled = true, settings = excluded.settings;
                insert into questions(tenant_id, id, kind, kind_version, family, lang, category_id, level, prompt, payload, fingerprint, status, source)
                select @t, gen_random_uuid(), 'choice', 1, 'trivia', 'en', @category, 1 + (n % 5), 'Lobby prompt ' || n,
                       '{"options":["a","b","c","d"],"correctIndex":0}'::jsonb, 'lobby-' || n, 'approved', 'admin'
                from generate_series(1, 60) as n
                """, new { t = Fixture.TenantId, category });
        }

        public async Task DisposeAsync()
        {
            if (Fixture is not null) await Fixture.DisposeAsync();
        }
    }

    private sealed class LobbyFeature : IFeature
    {
        public string Id => "session_lobby";
        public FeatureSettings Settings => FeatureSettings.Create(new LobbySettings(), _ => []);
        public void Configure(IFeatureBuilder feature) { }
    }

    private sealed record LobbySettings;

    private sealed class LobbyFormat(string id, Pacing pacing, bool guests) : IFormat
    {
        public string Id => id;
        public FormatSpec Spec { get; } = new(pacing, 2, 4, true, false, _ => true, guests, null, false, [new Role("player")], TimeSpan.Zero, new Dictionary<string, Type>());
        public FormatVersions Versions { get; } = new(1, 1, 1);
        public JsonElement ReadRules(JsonElement raw) => raw.ValueKind == JsonValueKind.Object ? raw.Clone() : throw new ArgumentException("Rules must be an object.");
        public DrawPlan Plan(ContentSpec content, JsonElement rules) => new(content.Count, ["choice"]);
        public void Start(ISession session)
        {
            if (pacing == Pacing.Shared) session.SetPosition(0, 0);
            else foreach (var seat in session.Seats) session.Serve(seat.Player, 0);
        }
        public void Apply(ISession session, Command command)
        {
            if (command.Type == CommandType.Answer) session.Record(session.Grade(command), 1);
        }
        public void Timeout(ISession session) { }
        public object View(IViewing viewing) => new { };
        public JsonElement UpgradeState(int from, JsonElement state) => state;
        public JsonElement UpgradeRules(int from, JsonElement rules) => rules;
        public JsonElement UpgradeResult(int from, JsonElement result) => result;
    }
}
