using LocalAI.Core.Interfaces;
using LocalAI.Providers.Documents;
using LocalAI.Providers.Ollama;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace LocalAI.Providers;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers Ollama as the <see cref="IChatProvider"/> implementation.</summary>
    public static IServiceCollection AddOllamaProvider(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OllamaOptions>()
            .Bind(configuration.GetSection(OllamaOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<OllamaOptions>, OllamaOptionsValidator>());

        services.AddHttpClient(OllamaClient.HttpClientName, (sp, http) =>
            {
                var baseUrl = sp.GetRequiredService<IOptions<OllamaOptions>>().Value.BaseUrl!.ToString();
                http.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
                // Timeouts are enforced per call (metadata vs. inference) by OllamaClient.
                http.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
            {
                ConnectTimeout = sp.GetRequiredService<IOptions<OllamaOptions>>().Value.ConnectTimeout,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            });

        services.TryAddSingleton<OllamaClient>();
        services.TryAddSingleton<OllamaProvider>();
        services.TryAddSingleton<IChatProvider>(sp => sp.GetRequiredService<OllamaProvider>());
        services.TryAddSingleton<IEmbeddingProvider>(sp => sp.GetRequiredService<OllamaProvider>());
        services.AddSingleton<ITextExtractor, PdfTextExtractor>();
        services.AddSingleton<ITextExtractor, PlainTextExtractor>();
        return services;
    }
}
