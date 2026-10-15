using LocalAI.Core.Exceptions;
using LocalAI.Core.Services;

namespace LocalAI.Api.Middleware;

public sealed record ApiError(int Status, string Code, string Title, bool Retryable);

/// <summary>Single source of truth for how domain errors map to HTTP and SSE error payloads.</summary>
public static class ErrorCatalog
{
    public static ApiError For(InferenceException exception) => exception.Code switch
    {
        InferenceErrorCode.ProviderUnavailable => Make(StatusCodes.Status503ServiceUnavailable, "Inference provider unavailable"),
        InferenceErrorCode.ProviderTimeout => Make(StatusCodes.Status504GatewayTimeout, "Inference timed out"),
        InferenceErrorCode.ProviderInvalidResponse => Make(StatusCodes.Status502BadGateway, "Invalid provider response"),
        InferenceErrorCode.ProviderError => Make(StatusCodes.Status502BadGateway, "Inference provider error"),
        InferenceErrorCode.ModelNotFound => Make(StatusCodes.Status404NotFound, "Model not found"),
        InferenceErrorCode.ModelNotAllowed => Make(StatusCodes.Status403Forbidden, "Model not allowed"),
        InferenceErrorCode.ModelLoadFailed => Make(StatusCodes.Status502BadGateway, "Model failed to load"),
        _ => Internal,
    } with
    {
        Code = InferenceMetrics.ToCode(exception.Code),
        Retryable = exception.IsTransient,
    };

    public static ApiError For(ConversationException exception) => exception.Code switch
    {
        ConversationErrorCode.NotFound =>
            new(StatusCodes.Status404NotFound, "conversation_not_found", "Conversation not found", false),
        ConversationErrorCode.Busy =>
            new(StatusCodes.Status409Conflict, "conversation_busy", "Conversation busy", true),
        ConversationErrorCode.NothingToRegenerate =>
            new(StatusCodes.Status409Conflict, "nothing_to_regenerate", "Nothing to regenerate", false),
        _ => Internal,
    };

    public static ApiError For(AuthException exception) => exception.Code switch
    {
        AuthErrorCode.InvalidCredentials => new(StatusCodes.Status401Unauthorized, "invalid_credentials", "Invalid credentials", false),
        AuthErrorCode.TooManyAttempts => new(StatusCodes.Status429TooManyRequests, "too_many_attempts", "Too many attempts", true),
        AuthErrorCode.SetupCompleted => new(StatusCodes.Status409Conflict, "setup_completed", "Setup already completed", false),
        AuthErrorCode.UsernameTaken => new(StatusCodes.Status409Conflict, "username_taken", "Username taken", false),
        AuthErrorCode.UserNotFound => new(StatusCodes.Status404NotFound, "user_not_found", "User not found", false),
        AuthErrorCode.LastAdmin => new(StatusCodes.Status409Conflict, "last_admin", "Last administrator", false),
        AuthErrorCode.CannotModifySelf => new(StatusCodes.Status409Conflict, "cannot_modify_self", "Cannot modify own account", false),
        _ => Internal,
    };

    public static readonly ApiError Unauthenticated =
        new(StatusCodes.Status401Unauthorized, "unauthenticated", "Sign-in required", false);

    public static readonly ApiError Forbidden =
        new(StatusCodes.Status403Forbidden, "forbidden", "Forbidden", false);

    public static readonly ApiError Internal =
        new(StatusCodes.Status500InternalServerError, "internal_error", "Internal server error", false);

    public const string InternalMessage = "An unexpected error occurred. Check the API logs for details.";

    private static ApiError Make(int status, string title) => new(status, string.Empty, title, false);
}
