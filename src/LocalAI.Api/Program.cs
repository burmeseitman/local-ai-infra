using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using LocalAI.Api.Auth;
using LocalAI.Api.Configuration;
using LocalAI.Api.Endpoints;
using LocalAI.Api.Middleware;
using LocalAI.Core;
using LocalAI.Persistence;
using LocalAI.Providers;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

// ---- Configuration (validated at startup; invalid settings stop the process with a clear message)
services.AddOptions<ApiOptions>()
    .Bind(builder.Configuration.GetSection(ApiOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
services.AddSingleton<IValidateOptions<ApiOptions>, ApiOptionsValidator>();
services.AddSingleton<ApiRuntimeInfo>();
services.AddOptions<AuthSessionOptions>()
    .Bind(builder.Configuration.GetSection(AuthSessionOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// ---- Application + provider
services.AddLocalAiCore(builder.Configuration);
services.AddOllamaProvider(builder.Configuration);
services.AddSqlitePersistence(builder.Configuration, builder.Environment.ContentRootPath);

// ---- HTTP surface
services.AddOptions<KestrelServerOptions>()
    .Configure<IOptions<ApiOptions>>((kestrel, api) =>
    {
        kestrel.AddServerHeader = false;
        kestrel.Limits.MaxRequestBodySize = api.Value.MaxRequestBodyBytes;
    });

services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

// Malformed bodies throw BadHttpRequestException in every environment, so they get Problem Details too.
services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
    ctx.ProblemDetails.Extensions.TryAdd("traceId", Activity.Current?.Id ?? ctx.HttpContext.TraceIdentifier));
services.AddExceptionHandler<ApiExceptionHandler>();

// CORS is only active when explicit origins are configured (not needed behind the same-origin proxy).
services.AddCors();
services.AddOptions<CorsOptions>()
    .Configure<IOptions<ApiOptions>>((cors, api) =>
    {
        if (api.Value.CorsAllowedOrigins.Length > 0)
        {
            // Credentials are needed for the session cookie; safe because origins are explicit.
            cors.AddDefaultPolicy(policy => policy
                .WithOrigins(api.Value.CorsAllowedOrigins)
                .WithMethods("GET", "POST", "PATCH", "DELETE")
                .WithHeaders("Content-Type", "Accept")
                .AllowCredentials());
        }
    });

// ---- Authentication: local accounts, cookie sessions (HttpOnly, SameSite=Strict).
// Data Protection keys sign the cookie; they are persisted so sessions survive restarts.
services.AddDataProtection().SetApplicationName("LocalAI");
services.AddOptions<KeyManagementOptions>()
    .Configure<IOptions<AuthSessionOptions>, IHostEnvironment, ILoggerFactory>((keys, session, env, loggers) =>
    {
        var path = Path.GetFullPath(Path.Combine(env.ContentRootPath, session.Value.KeysDirectory));
        keys.XmlRepository = new FileSystemXmlRepository(Directory.CreateDirectory(path), loggers);
    });

services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
    .Configure<IOptions<AuthSessionOptions>>((cookie, session) =>
    {
        cookie.Cookie.Name = session.Value.CookieName;
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SameSite = SameSiteMode.Strict;
        cookie.Cookie.SecurePolicy = session.Value.SecureCookie ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        cookie.ExpireTimeSpan = session.Value.Lifetime;
        cookie.SlidingExpiration = true;
        cookie.Events = new CookieAuthenticationEvents
        {
            OnValidatePrincipal = SessionPrincipal.ValidateAsync,
            // An API never redirects to a login page: answer with Problem Details instead.
            OnRedirectToLogin = context => WriteProblemAsync(context.HttpContext, ErrorCatalog.Unauthenticated, "Sign in to use this endpoint."),
            OnRedirectToAccessDenied = context => WriteProblemAsync(context.HttpContext, ErrorCatalog.Forbidden, "Administrator role required."),
        };
    });

services.AddAuthorizationBuilder()
    // Secure by default: every endpoint requires a session unless marked AllowAnonymous.
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(SessionPrincipal.AdminPolicy, policy => policy.RequireRole(nameof(LocalAI.Core.Models.UserRole.Admin)));

// Local hardware handles few generations at once; reject the excess instead of queueing unboundedly.
services.AddRateLimiter(_ => { });
services.AddOptions<RateLimiterOptions>()
    .Configure<IOptions<ApiOptions>>((limiter, api) =>
    {
        limiter.AddConcurrencyLimiter(ChatEndpoints.InferencePolicy, o =>
        {
            o.PermitLimit = api.Value.MaxConcurrentGenerations;
            o.QueueLimit = 0;
        });
        limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        limiter.OnRejected = async (context, cancellationToken) =>
        {
            var problems = context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
            await problems.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context.HttpContext,
                ProblemDetails = ApiExceptionHandler.CreateProblem(
                    new ApiError(StatusCodes.Status429TooManyRequests, "too_many_generations", "Too many concurrent generations", true),
                    $"At most {api.Value.MaxConcurrentGenerations} generation(s) may run at once. Try again when one finishes."),
            });
        };
    });

services.AddEndpointsApiExplorer();
services.AddSwaggerGen(o => o.SwaggerDoc("v1", new() { Title = "Local AI Infrastructure API", Version = "v1" }));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.Use(static (context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    headers.XFrameOptions = "DENY";
    return next(context);
});
app.UseRequestSizeGuard();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

if (app.Services.GetRequiredService<IOptions<ApiOptions>>().Value.EnableSwagger)
{
    app.UseSwagger();
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/swagger/v1/swagger.json", "Local AI Infrastructure API v1"));
}

app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapStatusEndpoints();
app.MapChatEndpoints();
app.MapConversationEndpoints();

await app.Services.InitializeDatabaseAsync();
await app.RunAsync();

static Task WriteProblemAsync(HttpContext context, ApiError error, string detail)
{
    context.Response.StatusCode = error.Status;
    return context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
    {
        HttpContext = context,
        ProblemDetails = ApiExceptionHandler.CreateProblem(error, detail),
    }).AsTask();
}

/// <summary>Entry point; partial so integration tests can use WebApplicationFactory&lt;Program&gt;.</summary>
public partial class Program;
