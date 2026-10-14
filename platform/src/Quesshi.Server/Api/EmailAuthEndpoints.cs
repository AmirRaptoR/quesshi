using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quesshi.Application.Identity;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Otp;
using Quesshi.Plugin.Api;
using Quesshi.Server.Security;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Api;

/// <summary>Email-code sign-in routes. Registration is kept within the #245 core module surface.</summary>
public sealed class EmailAuthEndpoints : ICoreRouteModule
{
    public void Map(CoreRouteBuilder routes)
    {
        var services = ((IEndpointRouteBuilder)routes.OpenRoutes).ServiceProvider;
        // The route-only mapping tests do not compose database services. The real host does, and
        // therefore rejects an unsafe delivery configuration while it is starting.
        if (services.GetService<DatabaseSources>() is not null)
        {
            EmailCodeDelivery.Validate(services.GetRequiredService<IConfiguration>(), services.GetRequiredService<IHostEnvironment>());
        }

        routes.OpenRoutes.MapPost("/auth/code", RequestCodeAsync);
        routes.OpenRoutes.MapPost("/auth/verify", VerifyCodeAsync);
        routes.OpenRoutes.MapPost("/auth/profile", ConfirmProfileAsync);
    }

    private static async Task<IResult> RequestCodeAsync(CodeRequest request, HttpContext context, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null)
        {
            return Results.NotFound();
        }

        var service = Create(context.RequestServices);
        var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!await service.RequestCodeAsync(request.Email ?? string.Empty, address, tenant.DefaultLanguage, cancellationToken))
        {
            return Results.BadRequest(new { error = "invalid_email" });
        }

        // A deliberately identical response covers unregistered, registered, and rate-limited mailboxes.
        return Results.Ok(new { status = "accepted" });
    }

    private static async Task<IResult> VerifyCodeAsync(VerifyRequest request, HttpContext context, IPlayerSessionIssuer sessions, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null)
        {
            return Results.NotFound();
        }

        var service = Create(context.RequestServices);
        var accountId = await service.VerifyCodeAsync(request.Email ?? string.Empty, request.Code ?? string.Empty, cancellationToken);
        if (accountId is null)
        {
            return Results.Unauthorized();
        }

        var player = await service.GetTenantPlayerAsync(tenant.Id, accountId.Value, cancellationToken);
        if (player.State == TenantPlayerState.Unavailable)
        {
            return Results.Forbid();
        }
        if (player.State == TenantPlayerState.Live)
        {
            await sessions.IssueAccountAsync(context, tenant.Id, accountId.Value, cancellationToken);
            return Results.Ok(new { status = "signed_in" });
        }

        var seed = await service.GetLatestProfileAsync(accountId.Value, cancellationToken);
        return Results.Ok(new
        {
            status = "profile_needed",
            token = service.CreateProfileToken(tenant.Id, accountId.Value),
            displayName = seed?.DisplayName,
            avatarSeed = seed?.AvatarSeed,
            lang = seed?.Language ?? tenant.DefaultLanguage
        });
    }

    private static async Task<IResult> ConfirmProfileAsync(ProfileRequest request, HttpContext context, IPlayerSessionIssuer sessions, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null)
        {
            return Results.NotFound();
        }

        var service = Create(context.RequestServices);
        if (!await service.IsProfileValidAsync(tenant.Id, request.DisplayName ?? string.Empty, request.AvatarSeed ?? string.Empty, request.Lang ?? string.Empty, cancellationToken))
        {
            return Results.BadRequest(new { error = "invalid_profile" });
        }

        var accountId = await service.ConsumeProfileTokenAsync(request.Token ?? string.Empty, tenant.Id, cancellationToken);
        if (accountId is null)
        {
            return Results.Unauthorized();
        }

        var result = await service.ConfirmProfileAsync(tenant.Id, accountId.Value, request.DisplayName ?? string.Empty, request.AvatarSeed ?? string.Empty, request.Lang ?? string.Empty, cancellationToken);
        if (result.State is ProfileConfirmationState.Invalid)
        {
            return Results.BadRequest(new { error = "invalid_profile" });
        }
        if (result.State is ProfileConfirmationState.Unavailable)
        {
            return Results.Forbid();
        }

        await sessions.IssueAccountAsync(context, tenant.Id, accountId.Value, cancellationToken);
        return Results.Ok(new { status = "signed_in" });
    }

    private static EmailAuthService Create(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var options = new EmailAuthOptions(
            configuration.GetValue("EmailAuth:EmailRateLimit", EmailAuthOptions.Default.EmailLimit),
            configuration.GetValue("EmailAuth:AddressRateLimit", EmailAuthOptions.Default.AddressLimit),
            TimeSpan.FromSeconds(configuration.GetValue("EmailAuth:RateLimitWindowSeconds", (int)EmailAuthOptions.Default.RateLimitWindow.TotalSeconds)),
            TimeSpan.FromSeconds(configuration.GetValue("EmailAuth:ProfileTokenLifetimeSeconds", (int)EmailAuthOptions.Default.ProfileTokenLifetime.TotalSeconds)));
        return new EmailAuthService(
            services.GetRequiredService<DatabaseSources>(),
            services.GetRequiredService<ITenantDb>(),
            EmailAuthService.SecretFromEnvironment(),
            options,
            new EmailCodeDelivery(configuration, services.GetRequiredService<ILogger<EmailCodeDelivery>>()));
    }

    private sealed record CodeRequest(string? Email);
    private sealed record VerifyRequest(string? Email, string? Code);
    private sealed record ProfileRequest(string? Token, string? DisplayName, string? AvatarSeed, string? Lang);
}
