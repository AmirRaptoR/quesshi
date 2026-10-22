using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Quesshi.Application;
using Quesshi.Plugin.Api;
using Quesshi.Server.Security;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Api;

/// <summary>Owner administration routes for platform-provisioned tenant capabilities.</summary>
public sealed class CapabilityEndpoints : ICoreRouteModule
{
    public void Map(CoreRouteBuilder routes)
    {
        var capabilities = routes.AdminRoutes(AdminRole.Owner).MapGroup("/capabilities");
        capabilities.MapGet("", ListAsync);
        capabilities.MapPut("/{type}/{key}", UpdateAsync).WithMetadata(new AdminAuditMetadata("admin.capability.update", "capability"));
    }

    private static async Task<IResult> ListAsync(
        HttpContext context,
        [FromServices] CapabilityManagementService capabilities,
        CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        return tenant is null
            ? Results.NotFound()
            : Results.Ok(await capabilities.ListAsync(tenant.Id, cancellationToken));
    }

    private static async Task<IResult> UpdateAsync(
        string type,
        string key,
        CapabilityUpdateRequest? request,
        HttpContext context,
        [FromServices] CapabilityManagementService capabilities,
        CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        if (request is null)
            return Error("invalid_request", StatusCodes.Status400BadRequest);
        if (request.Entitled is not null) return Error("entitlement_platform_controlled", StatusCodes.Status403Forbidden);
        if (request.Enabled is null || request.Settings is null || request.AdditionalProperties is { Count: > 0 })
            return Error("invalid_request", StatusCodes.Status400BadRequest);
        if (!TryType(type, out var capabilityType)) return Results.NotFound();

        var result = await capabilities.UpdateAsync(tenant.Id, capabilityType, key, request.Enabled.Value, request.Settings.Value, cancellationToken);
        return result.Status switch
        {
            CapabilityManagementStatus.Success => Results.NoContent(),
            CapabilityManagementStatus.NotFound => Results.NotFound(),
            CapabilityManagementStatus.Invalid => Results.Json(new { error = "invalid_settings", messages = result.Messages }, statusCode: StatusCodes.Status400BadRequest),
            _ => Error("capability_update_failed", StatusCodes.Status400BadRequest),
        };
    }

    private static bool TryType(string value, out CapabilityType type) =>
        Enum.TryParse(value, ignoreCase: true, out type) && Enum.IsDefined(type);

    private static IResult Error(string error, int statusCode) => Results.Json(new { error }, statusCode: statusCode);

    private sealed record CapabilityUpdateRequest(bool? Enabled, JsonElement? Settings, JsonElement? Entitled)
    {
        [JsonExtensionData]
        public IDictionary<string, JsonElement>? AdditionalProperties { get; init; }
    }
}
