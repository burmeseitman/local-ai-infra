using LocalAI.Api.Configuration;
using Microsoft.Extensions.Options;

namespace LocalAI.Api.Middleware;

/// <summary>
/// Rejects requests whose declared Content-Length exceeds the limit before the body is read.
/// Kestrel's MaxRequestBodySize (configured in Program) still bounds chunked bodies.
/// </summary>
internal static class RequestSizeGuard
{
    public static IApplicationBuilder UseRequestSizeGuard(this IApplicationBuilder app) =>
        app.Use(static async (context, next) =>
        {
            var limit = context.RequestServices.GetRequiredService<IOptions<ApiOptions>>().Value.MaxRequestBodyBytes;
            if (context.Request.ContentLength > limit)
            {
                throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);
            }

            await next(context);
        });
}
