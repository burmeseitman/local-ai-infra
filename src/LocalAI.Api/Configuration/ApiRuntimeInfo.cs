using System.Reflection;

namespace LocalAI.Api.Configuration;

/// <summary>Process-level facts reported by health and overview endpoints.</summary>
public sealed class ApiRuntimeInfo(IHostEnvironment environment, TimeProvider timeProvider)
{
    public DateTimeOffset StartedAt { get; } = timeProvider.GetUtcNow();

    public string Environment => environment.EnvironmentName;

    public string Version { get; } =
        typeof(ApiRuntimeInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "unknown";

    public TimeSpan Uptime => timeProvider.GetUtcNow() - StartedAt;
}
