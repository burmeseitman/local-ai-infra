namespace LocalAI.Core.Exceptions;

public enum AuthErrorCode
{
    InvalidCredentials,
    TooManyAttempts,
    SetupCompleted,
    UsernameTaken,
    UserNotFound,
    LastAdmin,
    CannotModifySelf,
}

/// <summary>Account and sign-in failures. <see cref="Exception.Message"/> is safe to show to clients.</summary>
public sealed class AuthException(AuthErrorCode code, string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public AuthErrorCode Code { get; } = code;

    /// <summary>Set for <see cref="AuthErrorCode.TooManyAttempts"/>.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;

    // Deliberately identical for "unknown user" and "wrong password" to avoid revealing which usernames exist.
    public static AuthException InvalidCredentials() =>
        new(AuthErrorCode.InvalidCredentials, "The username or password is incorrect.");
}
