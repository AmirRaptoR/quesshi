using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quesshi.Plugin.Api;

namespace Quesshi.Features.SignIn.Google;

/// <summary>Google ID-token sign-in supplied as a tenant capability.</summary>
public sealed class GoogleSignIn : ISignIn, IPublicSignInSettings
{
    public string Id => "google";

    public FeatureSettings Settings { get; } = FeatureSettings.Create(
        new GoogleSignInSettings("not-configured.apps.googleusercontent.com"),
        settings => IsClientId(settings.ClientId) ? [] : ["A Google client id is required."]);

    public void MapOpenRoutes(RouteGroupBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPost(string.Empty, SignInAsync);
    }

    public object GetPublicSettings(object settings) => settings is GoogleSignInSettings googleSettings
        ? new GooglePublicSettings(googleSettings.ClientId)
        : throw new ArgumentException("Google sign-in settings have the wrong type.", nameof(settings));

    private static async Task<IResult> SignInAsync(
        HttpContext context,
        GoogleIdTokenValidator validator,
        IIdentity identity,
        IExternalSignInContext signInContext,
        CancellationToken cancellationToken)
    {
        var tenant = signInContext.GetTenant(context);
        if (tenant is null)
        {
            return Results.NotFound();
        }

        GoogleSignInRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<GoogleSignInRequest>(cancellationToken);
        }
        catch (JsonException)
        {
            return Results.Unauthorized();
        }
        catch (NotSupportedException)
        {
            return Results.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request?.IdToken))
        {
            return Results.Unauthorized();
        }

        var identityToken = await validator.ValidateAsync(request.IdToken, context, cancellationToken);
        if (identityToken is null)
        {
            return Results.Unauthorized();
        }

        var completion = await identity.CompleteExternalAsync(
            tenant,
            "google",
            identityToken.Subject,
            identityToken.Email,
            identityToken.EmailVerified,
            cancellationToken);
        if (completion.State is ExternalIdentityCompletionState.Unavailable)
        {
            return Results.Forbid();
        }
        if (completion.State is ExternalIdentityCompletionState.SignedIn && completion.AccountId is { } accountId)
        {
            await signInContext.IssueAccountSessionAsync(context, tenant.Id, accountId, cancellationToken);
            return Results.Ok(new { status = "signed_in" });
        }
        if (completion.State is ExternalIdentityCompletionState.ProfileNeeded && completion.Profile is { } profile)
        {
            return Results.Ok(new
            {
                status = "profile_needed",
                token = profile.Token,
                displayName = profile.DisplayName,
                avatarSeed = profile.AvatarSeed,
                lang = profile.Language
            });
        }

        throw new InvalidOperationException("External identity completion returned an incomplete result.");
    }

    private sealed record GoogleSignInRequest(string? IdToken);

    private static bool IsClientId(string? clientId) =>
        !string.IsNullOrWhiteSpace(clientId)
        && clientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal)
        && !clientId.Any(char.IsWhiteSpace);
}

/// <summary>Per-tenant public settings for Google sign-in.</summary>
public sealed record GoogleSignInSettings(string ClientId);

/// <summary>The browser-safe Google settings advertised in tenant configuration.</summary>
public sealed record GooglePublicSettings(string ClientId);

/// <summary>A validated Google ID-token subject and optional email claims.</summary>
public sealed record GoogleIdentityToken(string Subject, string? Email, bool EmailVerified);

/// <summary>Validates RS256 Google ID tokens against Google's fixed public-key endpoint.</summary>
public sealed class GoogleIdTokenValidator(HttpClient client, TimeProvider timeProvider)
{
    public static readonly Uri KeySetAddress = new("https://www.googleapis.com/oauth2/v3/certs");

    public async Task<GoogleIdentityToken?> ValidateAsync(string idToken, HttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idToken);
        ArgumentNullException.ThrowIfNull(context);

        var settings = await LoadSettingsAsync(context, cancellationToken);
        if (settings is null || !TryReadToken(idToken, settings.ClientId, timeProvider.GetUtcNow(), out var parsed))
        {
            return null;
        }

        GoogleKeys? keys;
        try
        {
            await using var stream = await client.GetStreamAsync(KeySetAddress, cancellationToken);
            keys = await JsonSerializer.DeserializeAsync<GoogleKeys>(stream, cancellationToken: cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        var key = keys?.Keys?.SingleOrDefault(candidate => string.Equals(candidate.Kid, parsed.KeyId, StringComparison.Ordinal));
        if (key is null || !string.Equals(key.Kty, "RSA", StringComparison.Ordinal) || !TryDecodeBase64Url(key.N, out var modulus) || !TryDecodeBase64Url(key.E, out var exponent))
        {
            return null;
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters { Modulus = modulus, Exponent = exponent });
            return rsa.VerifyData(Encoding.ASCII.GetBytes(parsed.SigningInput), parsed.Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                ? new GoogleIdentityToken(parsed.Subject, parsed.Email, parsed.EmailVerified)
                : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static async Task<GoogleSignInSettings?> LoadSettingsAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var capability = context.RequestServices.GetService(typeof(IGoogleSignInSettings)) as IGoogleSignInSettings;
        return capability is null ? null : await capability.GetAsync(context, cancellationToken);
    }

    private static bool TryReadToken(string token, string clientId, DateTimeOffset now, out ParsedToken parsed)
    {
        parsed = default!;
        var parts = token.Split('.');
        if (parts.Length != 3 || token.Length > 16_384
            || !TryDecodeBase64Url(parts[0], out var headerBytes)
            || !TryDecodeBase64Url(parts[1], out var payloadBytes)
            || !TryDecodeBase64Url(parts[2], out var signature))
        {
            return false;
        }

        try
        {
            using var header = JsonDocument.Parse(headerBytes);
            using var payload = JsonDocument.Parse(payloadBytes);
            if (!TryGetString(header.RootElement, "alg", out var algorithm) || !string.Equals(algorithm, "RS256", StringComparison.Ordinal)
                || !TryGetString(header.RootElement, "kid", out var keyId)
                || !TryGetString(payload.RootElement, "sub", out var subject)
                || !TryGetString(payload.RootElement, "iss", out var issuer)
                || (issuer is not "https://accounts.google.com" and not "accounts.google.com")
                || !TryGetString(payload.RootElement, "aud", out var audience)
                || !string.Equals(audience, clientId, StringComparison.Ordinal)
                || !TryGetUnixTime(payload.RootElement, "exp", out var expiresAt)
                || expiresAt <= now)
            {
                return false;
            }

            var email = TryGetString(payload.RootElement, "email", out var address) ? address : null;
            var emailVerified = payload.RootElement.TryGetProperty("email_verified", out var verified) && verified.ValueKind is JsonValueKind.True;
            parsed = new ParsedToken(keyId, subject, email, emailVerified, string.Concat(parts[0], ".", parts[1]), signature);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.String)
        {
            value = property.GetString() ?? "";
            return !string.IsNullOrWhiteSpace(value);
        }

        value = "";
        return false;
    }

    private static bool TryGetUnixTime(JsonElement element, string name, out DateTimeOffset value)
    {
        value = default;
        if (!element.TryGetProperty(name, out var property) || !property.TryGetInt64(out var seconds))
        {
            return false;
        }

        try
        {
            value = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryDecodeBase64Url(string? value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(value) || value.Length % 4 == 1 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            return false;
        }

        var base64 = value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4);
        try
        {
            bytes = Convert.FromBase64String(base64);
            return bytes.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed record GoogleKeys([property: JsonPropertyName("keys")] IReadOnlyList<GoogleKey>? Keys);

    private sealed record GoogleKey(
        [property: JsonPropertyName("kid")] string? Kid,
        [property: JsonPropertyName("kty")] string? Kty,
        [property: JsonPropertyName("n")] string? N,
        [property: JsonPropertyName("e")] string? E);

    private sealed record ParsedToken(string KeyId, string Subject, string? Email, bool EmailVerified, string SigningInput, byte[] Signature);
}

/// <summary>Reads the enabled tenant's Google client-id setting at the core boundary.</summary>
public interface IGoogleSignInSettings
{
    Task<GoogleSignInSettings?> GetAsync(HttpContext context, CancellationToken cancellationToken = default);
}
