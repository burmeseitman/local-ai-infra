using System.Globalization;
using System.Security.Claims;
using LocalAI.Core.Interfaces;
using LocalAI.Core.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace LocalAI.Api.Auth;

/// <summary>Builds and re-validates the claims stored in the session cookie.</summary>
public static class SessionPrincipal
{
    public const string AdminPolicy = "admin";
    private const string SessionVersionClaim = "localai:sv";

    public static ClaimsPrincipal Create(UserAccount account) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, account.Id.ToString()),
                new Claim(ClaimTypes.Name, account.Username),
                new Claim(ClaimTypes.Role, account.Role.ToString()),
                new Claim(SessionVersionClaim, account.SessionVersion.ToString(CultureInfo.InvariantCulture)),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme));

    public static Task SignInAsync(HttpContext context, UserAccount account) =>
        context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, Create(account));

    /// <summary>The signed-in user's id. Only call on endpoints that require authentication.</summary>
    public static Guid GetUserId(this ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("No authenticated user."));

    /// <summary>
    /// Runs on every authenticated request: the session is rejected if the account was
    /// deleted, or its password/role changed since sign-in (session version bumped).
    /// </summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        if (principal is null
            || !Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            || !int.TryParse(principal.FindFirstValue(SessionVersionClaim), CultureInfo.InvariantCulture, out var version))
        {
            context.RejectPrincipal();
            return;
        }

        var users = context.HttpContext.RequestServices.GetRequiredService<IUserStore>();
        var account = await users.FindByIdAsync(userId, context.HttpContext.RequestAborted);
        if (account is null || account.SessionVersion != version)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }
}
