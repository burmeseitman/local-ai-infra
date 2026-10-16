using LocalAI.Core.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace LocalAI.Api.Middleware;

/// <summary>
/// Converts exceptions on non-streaming endpoints into RFC 7807 Problem Details. Messages
/// are safe for clients; internals (stack traces, upstream errors) only go to logs.
/// </summary>
internal sealed class ApiExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            // The client went away; there is nobody to answer. 499 is the de-facto "client closed request".
            logger.LogInformation("Request {Path} was cancelled by the client", httpContext.Request.Path);
            httpContext.Response.StatusCode = 499;
            return true;
        }

        if (httpContext.Response.HasStarted)
        {
            logger.LogError(exception, "Unhandled error after the response started for {Path}", httpContext.Request.Path);
            return false;
        }

        ApiError error;
        string detail;
        switch (exception)
        {
            case InferenceException inference:
                error = ErrorCatalog.For(inference);
                detail = inference.Message;
                logger.LogWarning(
                    "Request {Path} failed: {Code} ({Detail})", httpContext.Request.Path, error.Code, inference.ProviderDetail ?? inference.Message);
                break;

            case ConversationException conversation:
                error = ErrorCatalog.For(conversation);
                detail = conversation.Message;
                logger.LogInformation("Request {Path} rejected: {Code}", httpContext.Request.Path, error.Code);
                break;

            case AuthException auth:
                error = ErrorCatalog.For(auth);
                detail = auth.Message;
                if (auth.RetryAfter is { } retryAfter)
                {
                    httpContext.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                logger.LogInformation("Request {Path} rejected: {Code}", httpContext.Request.Path, error.Code);
                break;

            case DocumentException document:
                error = ErrorCatalog.For(document);
                detail = document.Message;
                logger.LogInformation("Request {Path} rejected: {Code}", httpContext.Request.Path, error.Code);
                break;

            case BadHttpRequestException badRequest:
                var tooLarge = badRequest.StatusCode == StatusCodes.Status413PayloadTooLarge;
                error = new ApiError(
                    badRequest.StatusCode,
                    tooLarge ? "payload_too_large" : "invalid_request",
                    tooLarge ? "Payload too large" : "Invalid request",
                    false);
                detail = tooLarge
                    ? "The request body exceeds the configured size limit."
                    : "The request body is missing or is not valid JSON for this endpoint.";
                logger.LogInformation("Rejected malformed request to {Path}: {Reason}", httpContext.Request.Path, badRequest.Message);
                break;

            default:
                error = ErrorCatalog.Internal;
                detail = ErrorCatalog.InternalMessage;
                logger.LogError(exception, "Unhandled error for {Path}", httpContext.Request.Path);
                break;
        }

        httpContext.Response.StatusCode = error.Status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = CreateProblem(error, detail),
        });
    }

    public static ProblemDetails CreateProblem(ApiError error, string detail) => new()
    {
        Status = error.Status,
        Title = error.Title,
        Detail = detail,
        Extensions =
        {
            ["code"] = error.Code,
            ["retryable"] = error.Retryable,
        },
    };
}
