using LocalAI.Api.Configuration;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace LocalAI.Api.Middleware;

/// <summary>Endpoint metadata that raises the body-size limit for that endpoint (e.g. uploads).</summary>
public sealed record RequestBodyLimit(long Bytes);

/// <summary>
/// Rejects requests whose declared Content-Length exceeds the limit before the body is read,
/// and applies the same limit to Kestrel for chunked bodies. The limit is
/// <see cref="ApiOptions.MaxRequestBodyBytes"/> unless the endpoint declares a <see cref="RequestBodyLimit"/>.
/// </summary>
internal static class RequestSizeGuard
{
    public static IApplicationBuilder UseRequestSizeGuard(this IApplicationBuilder app) =>
        app.Use(static async (context, next) =>
        {
            var limit = context.GetEndpoint()?.Metadata.GetMetadata<RequestBodyLimit>()?.Bytes
                ?? context.RequestServices.GetRequiredService<IOptions<ApiOptions>>().Value.MaxRequestBodyBytes;
            if (context.Request.ContentLength > limit)
            {
                throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);
            }

            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            {
                feature.MaxRequestBodySize = limit;
            }

            await next(context);
        });
}

/// <summary>
/// CSRF defence for state-changing requests. Session cookies are SameSite=Strict, but "same
/// site" includes every port on localhost, and a page there could still submit a cross-origin
/// HTML form (urlencoded or multipart) with the cookie. Browsers cannot add custom headers to
/// such forms without a CORS preflight, which this API does not grant, so requiring one header
/// on unsafe methods blocks them. The dashboard sends it on every request.
/// </summary>
internal static class CsrfHeaderGuard
{
    public const string HeaderName = "X-LocalAI-Request";

    public static IApplicationBuilder UseCsrfHeaderGuard(this IApplicationBuilder app) =>
        app.Use(static async (context, next) =>
        {
            var method = context.Request.Method;
            var unsafeMethod = !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method));
            if (unsafeMethod
                && context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                && !context.Request.Headers.ContainsKey(HeaderName))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context,
                    ProblemDetails = ApiExceptionHandler.CreateProblem(
                        new ApiError(StatusCodes.Status403Forbidden, "missing_request_header", "Missing request header", false),
                        $"State-changing requests must include the {HeaderName} header."),
                });
                return;
            }

            await next(context);
        });
}
