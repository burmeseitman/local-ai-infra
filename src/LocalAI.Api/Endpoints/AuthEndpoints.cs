using LocalAI.Api.Auth;
using LocalAI.Api.Contracts;
using LocalAI.Core.Models;
using LocalAI.Core.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LocalAI.Api.Endpoints;

/// <summary>
/// Session endpoints. State, setup and login are anonymous; everything else in the API
/// requires a session (fallback authorization policy).
/// </summary>
internal static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Auth");

        auth.MapGet("/state", GetStateAsync)
            .AllowAnonymous()
            .WithName("GetAuthState")
            .WithSummary("Whether first-run setup is required, and the signed-in user (if any).");
        auth.MapPost("/setup", SetupAsync)
            .AllowAnonymous()
            .WithName("Setup")
            .WithSummary("Creates the first administrator and signs in. Only works while no account exists.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);
        auth.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .WithName("Login")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        auth.MapPost("/logout", (Delegate)LogoutAsync)
            .AllowAnonymous()
            .WithName("Logout");
        auth.MapPost("/password", ChangePasswordAsync)
            .WithName("ChangePassword")
            .WithSummary("Changes the caller's password. Signs out every other session.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return app;
    }

    private static async Task<AuthStateResponse> GetStateAsync(HttpContext context, AccountService accounts, CancellationToken cancellationToken)
    {
        var setupRequired = await accounts.IsSetupRequiredAsync(cancellationToken);
        UserAccount? user = null;
        if (context.User.Identity?.IsAuthenticated == true)
        {
            user = await accounts.FindAsync(context.User.GetUserId(), cancellationToken);
        }

        return new AuthStateResponse(setupRequired, user is null ? null : UserDto.From(user));
    }

    private static async Task<Results<Ok<UserDto>, ValidationProblem>> SetupAsync(
        CredentialsRequest body, HttpContext context, AccountService accounts, CancellationToken cancellationToken)
    {
        var errors = accounts.ValidateNewCredentials(body.Username, body.Password);
        if (errors.Count > 0)
        {
            return ApiValidation.Problem(errors);
        }

        var account = await accounts.SetupAsync(body.Username!, body.Password!, cancellationToken);
        await SessionPrincipal.SignInAsync(context, account);
        return TypedResults.Ok(UserDto.From(account));
    }

    private static async Task<Ok<UserDto>> LoginAsync(
        CredentialsRequest body, HttpContext context, AccountService accounts, CancellationToken cancellationToken)
    {
        var account = await accounts.SignInAsync(body.Username, body.Password, cancellationToken);
        await SessionPrincipal.SignInAsync(context, account);
        return TypedResults.Ok(UserDto.From(account));
    }

    private static async Task<NoContent> LogoutAsync(HttpContext context)
    {
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ValidationProblem>> ChangePasswordAsync(
        ChangePasswordRequest body, HttpContext context, AccountService accounts, CancellationToken cancellationToken)
    {
        if (accounts.ValidatePassword(body.NewPassword) is { } error)
        {
            return ApiValidation.Problem(new Dictionary<string, string[]> { ["newPassword"] = [error] });
        }

        var account = await accounts.ChangePasswordAsync(context.User.GetUserId(), body.CurrentPassword, body.NewPassword!, cancellationToken);
        // Re-issue this session with the new session version; all other sessions are now invalid.
        await SessionPrincipal.SignInAsync(context, account);
        return TypedResults.NoContent();
    }
}

internal static class ApiValidation
{
    public static ValidationProblem Problem(IDictionary<string, string[]> errors) =>
        TypedResults.ValidationProblem(
            errors,
            detail: "The request is invalid.",
            extensions: new Dictionary<string, object?> { ["code"] = "validation_failed", ["retryable"] = false });
}
