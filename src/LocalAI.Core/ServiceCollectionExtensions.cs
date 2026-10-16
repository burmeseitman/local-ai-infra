using LocalAI.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace LocalAI.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers provider-independent chat services. A provider must be registered separately.</summary>
    public static IServiceCollection AddLocalAiCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ChatOptions>()
            .Bind(configuration.GetSection(ChatOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ChatOptions>, ChatOptionsValidator>());

        services.AddOptions<AccountOptions>()
            .Bind(configuration.GetSection(AccountOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<InferenceMetrics>();
        services.TryAddSingleton<ModelCatalog>();
        services.TryAddSingleton<ProviderStatusService>();
        services.TryAddSingleton<ChatRequestValidator>();
        services.TryAddScoped<ChatService>();
        services.TryAddSingleton<ConversationLocks>();
        services.TryAddScoped<ConversationService>();
        services.TryAddSingleton<LoginThrottle>();

        services.AddOptions<RagOptions>()
            .Bind(configuration.GetSection(RagOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RagOptions>, RagOptionsValidator>());
        services.TryAddSingleton<DocumentIngestionChannel>();
        services.TryAddScoped<DocumentService>();
        services.TryAddScoped<DocumentRetriever>();
        services.AddHostedService<DocumentIngestionWorker>();
        services.TryAddScoped<AccountService>();
        return services;
    }
}
