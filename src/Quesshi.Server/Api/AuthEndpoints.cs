using System.Text.Json;
using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;
using Quesshi.Server.Auth;
using Quesshi.Shared;

namespace Quesshi.Server.Api;

public static class AuthEndpoints
{
    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapGet("/config", (AuthOptions auth) =>
            new AuthConfigDto(!string.IsNullOrWhiteSpace(auth.GoogleClientId), auth.GoogleClientId));

        group.MapPost("/otp/request", async (OtpRequestDto body, AuthService service) =>
        {
            if (!EmailAddress.LooksValid(body.Email)) return Results.BadRequest(new { error = "invalid_email" });

            await service.RequestOtpAsync(body.Email, body.Lang.ToLanguage());

            // Always "sent", whether or not that address has an account: saying otherwise turns this
            // endpoint into a way to ask which addresses are registered.
            return Results.Ok(new OtpSentDto(true));
        });

        group.MapPost("/otp/verify", async (OtpVerifyDto body, AuthService service, TokenIssuer tokens) =>
        {
            var (result, player) = await service.VerifyOtpAsync(body.Email, body.Code, body.Lang.ToLanguage());
            if (result != OtpResult.Ok || player is null)
                return Results.BadRequest(new { error = result.ToString().ToLowerInvariant() });

            return Results.Ok(SignIn(player, tokens));
        });

        // --- guests ------------------------------------------------------------------
        // Anonymous on purpose: an invite link has to say who is challenging before anyone is asked
        // to identify themselves. It reveals nothing a person holding the code should not see.
        app.MapGet("/api/invite/{code}", async (string code, IMatchArchive archive, IPlayerRepository players) =>
        {
            var match = await archive.ByCodeAsync(code.Trim().ToUpperInvariant());
            if (match is null) return Results.NotFound(new { error = "no_such_code" });

            var challenger = await players.GetAsync(match.ChallengerId);
            return Results.Ok(new InviteDto(match.Code, challenger?.DisplayName ?? "—",
                challenger?.AvatarSeed ?? match.ChallengerId, match.QuestionIds.Count,
                match.State == MatchState.AwaitingOpponent));
        });

        // Becoming a guest and taking the seat are one call, so a name typed against a duel that has
        // already been taken never leaves a player record behind.
        group.MapPost("/guest/{code}", async (string code, GuestJoinDto body, IMatchArchive archive,
            IPlayerRepository players, IGrainFactory grains, TokenIssuer tokens, IIdFactory ids, IClock clock) =>
        {
            var name = body.Name?.Trim() ?? "";
            if (name.Length is < 2 or > 24) return Results.BadRequest(new { error = "name_length" });

            var found = await archive.ByCodeAsync(code.Trim().ToUpperInvariant());
            if (found is null) return Results.NotFound(new { error = "no_such_code" });
            if (found.State != MatchState.AwaitingOpponent) return Results.BadRequest(new { error = "cannot_join" });

            var guest = Player.Guest(ids.NewId(), name, body.Lang.ToLanguage(), clock.Now);
            await players.UpsertAsync(guest);

            var grain = grains.GetGrain<IMatchGrain>(found.Id);
            if (!await grain.JoinAsync(guest.Id)) return Results.BadRequest(new { error = "cannot_join" });

            var view = await grain.GetAsync(guest.Id);
            if (view is null) return Results.BadRequest(new { error = "cannot_join" });

            var challenger = await players.GetAsync(found.ChallengerId);
            var summary = view.ToSummary(guest.Id, id => id == guest.Id
                ? (guest.DisplayName, guest.AvatarSeed)
                : (challenger?.DisplayName ?? "—", challenger?.AvatarSeed ?? id));

            return Results.Ok(new GuestResultDto(tokens.Issue(guest), guest.ToMeDto([]), summary));
        });

        group.MapPost("/google", async (GoogleSignInDto body, AuthOptions auth, AuthService service,
            TokenIssuer tokens, IHttpClientFactory http, ILoggerFactory logs) =>
        {
            if (string.IsNullOrWhiteSpace(auth.GoogleClientId)) return Results.BadRequest(new { error = "google_disabled" });

            var profile = await VerifyGoogleTokenAsync(body.IdToken, auth.GoogleClientId, http, logs);
            if (profile is null) return Results.BadRequest(new { error = "invalid_token" });

            var player = await service.SignInWithGoogleAsync(profile.Value.Email, profile.Value.Name, body.Lang.ToLanguage());
            if (player is null) return Results.BadRequest(new { error = "banned" });

            return Results.Ok(SignIn(player, tokens));
        });
    }

    /// <summary>
    /// Google signs the id_token; asking Google to decode it is the whole verification. We still
    /// check the audience ourselves, because a token minted for another app would otherwise pass.
    /// </summary>
    private static async Task<(string Email, string? Name)?> VerifyGoogleTokenAsync(string idToken, string clientId,
        IHttpClientFactory http, ILoggerFactory logs)
    {
        var logger = logs.CreateLogger("GoogleSignIn");
        try
        {
            using var client = http.CreateClient();
            var response = await client.GetAsync($"https://oauth2.googleapis.com/tokeninfo?id_token={Uri.EscapeDataString(idToken)}");
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            if (!root.TryGetProperty("aud", out var aud) || aud.GetString() != clientId) return null;
            if (!root.TryGetProperty("email_verified", out var verified) || verified.GetString() != "true") return null;
            if (!root.TryGetProperty("email", out var email)) return null;

            return (email.GetString()!, root.TryGetProperty("name", out var name) ? name.GetString() : null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Google token verification failed");
            return null;
        }
    }

    private static AuthResultDto SignIn(Player player, TokenIssuer tokens) => new(tokens.Issue(player), player.ToMeDto([]));

}

