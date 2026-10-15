using LocalAI.Api.Auth;
using LocalAI.Api.Contracts;
using LocalAI.Core.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LocalAI.Api.Endpoints;

/// <summary>Account administration. Administrators only.</summary>
internal static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var users = app.MapGroup("/api/users")
            .WithTags("Users")
            .RequireAuthorization(SessionPrincipal.AdminPolicy);

        users.MapGet("/", ListAsync).WithName("ListUsers").WithAuthErrors();
        users.MapPost("/", CreateAsync)
            .WithName("CreateUser")
            .WithAuthErrors()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);
        users.MapPatch("/{id:guid}", UpdateAsync)
            .WithName("UpdateUser")
            .WithAuthErrors()
            .WithSummary("Change another user's role and/or reset their password (signs them out).")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        users.MapDelete("/{id:guid}", DeleteAsync)
            .WithName("DeleteUser")
            .WithAuthErrors()
            .WithSummary("Deletes a user and all of their conversations.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static RouteHandlerBuilder WithAuthErrors(this RouteHandlerBuilder builder) => builder
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    private static async Task<UserListResponse> ListAsync(AccountService accounts, CancellationToken cancellationToken) =>
        new((await accounts.ListAsync(cancellationToken)).Select(UserDto.From).ToArray());

    private static async Task<Results<Created<UserDto>, ValidationProblem>> CreateAsync(
        CreateUserRequest body, AccountService accounts, CancellationToken cancellationToken)
    {
        var errors = accounts.ValidateNewCredentials(body.Username, body.Password);
        if (errors.Count > 0)
        {
            return ApiValidation.Problem(errors);
        }

        var account = await accounts.CreateUserAsync(body.Username!, body.Password!, body.Role, cancellationToken);
        return TypedResults.Created($"/api/users/{account.Id}", UserDto.From(account));
    }

    private static async Task<Results<NoContent, ValidationProblem>> UpdateAsync(
        Guid id, UpdateUserRequest body, HttpContext context, AccountService accounts, CancellationToken cancellationToken)
    {
        if (body.Password is not null && accounts.ValidatePassword(body.Password) is { } error)
        {
            return ApiValidation.Problem(new Dictionary<string, string[]> { ["password"] = [error] });
        }

        await accounts.UpdateUserAsync(context.User.GetUserId(), id, body.Role, body.Password, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> DeleteAsync(Guid id, HttpContext context, AccountService accounts, CancellationToken cancellationToken)
    {
        await accounts.DeleteUserAsync(context.User.GetUserId(), id, cancellationToken);
        return TypedResults.NoContent();
    }
}
