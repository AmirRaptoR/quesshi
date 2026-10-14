using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Quesshi.Server.Accounts;
using Quesshi.Server.Security;

namespace Quesshi.Server.Api;

/// <summary>Authenticated account self-deletion route.</summary>
public sealed class AccountDeletionEndpoints : ICoreRouteModule
{
    public void Map(CoreRouteBuilder routes) => routes.PlayerRoutes.MapDelete("/me", DeleteAsync);

    private static async Task<IResult> DeleteAsync(
        HttpContext context,
        [FromServices] AccountDeletionService deletion,
        CancellationToken cancellationToken)
    {
        var player = context.Player() ?? throw new InvalidOperationException("Account deletion requires player authentication.");
        if (player.IsGuest || player.AccountId is null) return Results.StatusCode(StatusCodes.Status403Forbidden);

        await deletion.DeleteAsync(player.AccountId.Value, cancellationToken);
        context.Response.Cookies.Delete(PlayerSessionService.CookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/"
        });
        return Results.NoContent();
    }
}
