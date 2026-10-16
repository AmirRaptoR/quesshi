using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Features.SignIn.Google;

namespace Quesshi.Features.Tests;

public sealed class GoogleIdTokenValidatorTests
{
    private const string ClientId = "123456.apps.googleusercontent.com";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T12:00:00Z");

    [Fact]
    public async Task Valid_google_token_returns_subject_and_email_claims()
    {
        using var key = RSA.Create(2048);
        var validator = CreateValidator(key);

        var token = CreateToken(key, issuer: "https://accounts.google.com", audience: ClientId, expiresAt: Now.AddMinutes(5));

        var result = await validator.ValidateAsync(token, CreateContext());

        Assert.Equal(new GoogleIdentityToken("google-subject", "person@example.test", true), result);
    }

    [Fact]
    public async Task Token_with_a_bad_signature_is_rejected()
    {
        using var signingKey = RSA.Create(2048);
        using var trustedKey = RSA.Create(2048);
        var validator = CreateValidator(trustedKey);

        var result = await validator.ValidateAsync(CreateToken(signingKey, "https://accounts.google.com", ClientId, Now.AddMinutes(5)), CreateContext());

        Assert.Null(result);
    }

    [Fact]
    public async Task Token_with_a_wrong_issuer_is_rejected()
    {
        using var key = RSA.Create(2048);
        var validator = CreateValidator(key);

        var result = await validator.ValidateAsync(CreateToken(key, "https://issuer.example.test", ClientId, Now.AddMinutes(5)), CreateContext());

        Assert.Null(result);
    }

    [Fact]
    public async Task Token_with_a_wrong_audience_is_rejected()
    {
        using var key = RSA.Create(2048);
        var validator = CreateValidator(key);

        var result = await validator.ValidateAsync(CreateToken(key, "https://accounts.google.com", "other.apps.googleusercontent.com", Now.AddMinutes(5)), CreateContext());

        Assert.Null(result);
    }

    [Fact]
    public async Task Token_with_an_expired_lifetime_is_rejected()
    {
        using var key = RSA.Create(2048);
        var validator = CreateValidator(key);

        var result = await validator.ValidateAsync(CreateToken(key, "https://accounts.google.com", ClientId, Now.AddSeconds(-1)), CreateContext());

        Assert.Null(result);
    }

    [Fact]
    public async Task Missing_google_client_id_setting_rejects_the_token()
    {
        using var key = RSA.Create(2048);
        var timeProvider = new FixedTimeProvider(Now);
        var validator = new GoogleIdTokenValidator(new HttpClient(new KeySetHandler(key)), timeProvider, new GoogleKeySetCache(timeProvider));
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };

        var result = await validator.ValidateAsync(CreateToken(key, "https://accounts.google.com", ClientId, Now.AddMinutes(5)), context);

        Assert.Null(result);
    }

    [Fact]
    public async Task Repeated_google_token_validations_reuse_the_cached_key_set()
    {
        using var key = RSA.Create(2048);
        var handler = new KeySetHandler(key);
        var timeProvider = new FixedTimeProvider(Now);
        var validator = new GoogleIdTokenValidator(new HttpClient(handler), timeProvider, new GoogleKeySetCache(timeProvider));
        var token = CreateToken(key, "https://accounts.google.com", ClientId, Now.AddMinutes(5));

        await validator.ValidateAsync(token, CreateContext());
        await validator.ValidateAsync(token, CreateContext());

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task An_unknown_key_identifier_refreshes_the_cached_key_set_once()
    {
        using var key = RSA.Create(2048);
        var handler = new KeySetHandler(key);
        var timeProvider = new FixedTimeProvider(Now);
        var validator = new GoogleIdTokenValidator(new HttpClient(handler), timeProvider, new GoogleKeySetCache(timeProvider));
        var knownToken = CreateToken(key, "https://accounts.google.com", ClientId, Now.AddMinutes(5));
        var unknownToken = CreateToken(key, "https://accounts.google.com", ClientId, Now.AddMinutes(5), keyId: "rotated-key");

        await validator.ValidateAsync(knownToken, CreateContext());
        await validator.ValidateAsync(unknownToken, CreateContext());
        await validator.ValidateAsync(unknownToken, CreateContext());

        Assert.Equal(2, handler.RequestCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("google-client")]
    [InlineData("client id.apps.googleusercontent.com")]
    public void Google_settings_reject_invalid_client_ids(string clientId)
    {
        var settings = new GoogleSignIn().Settings;

        var errors = settings.Validate(new GoogleSignInSettings(clientId));

        Assert.NotEmpty(errors);
    }

    private static GoogleIdTokenValidator CreateValidator(RSA key)
    {
        var timeProvider = new FixedTimeProvider(Now);
        return new GoogleIdTokenValidator(new HttpClient(new KeySetHandler(key)), timeProvider, new GoogleKeySetCache(timeProvider));
    }

    private static DefaultHttpContext CreateContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGoogleSignInSettings>(new TestSettings(ClientId));
        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
    }

    private static string CreateToken(RSA key, string issuer, string audience, DateTimeOffset expiresAt, string keyId = "test-key")
    {
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", kid = keyId }));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            sub = "google-subject",
            iss = issuer,
            aud = audience,
            exp = expiresAt.ToUnixTimeSeconds(),
            email = "person@example.test",
            email_verified = true
        }));
        var signed = string.Concat(header, ".", payload);
        var signature = key.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return string.Concat(signed, ".", Encode(signature));
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class TestSettings(string clientId) : IGoogleSignInSettings
    {
        public Task<GoogleSignInSettings?> GetAsync(HttpContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<GoogleSignInSettings?>(new GoogleSignInSettings(clientId));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class KeySetHandler(RSA key) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var parameters = key.ExportParameters(false);
            var body = JsonSerializer.Serialize(new
            {
                keys = new[]
                {
                    new { kid = "test-key", kty = "RSA", n = Encode(parameters.Modulus!), e = Encode(parameters.Exponent!) }
                }
            });
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            response.Headers.TryAddWithoutValidation("Cache-Control", "public, max-age=3600");
            return Task.FromResult(response);
        }
    }
}
