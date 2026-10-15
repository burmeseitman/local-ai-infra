# syntax=docker/dockerfile:1
# Build context: repository root.
#   docker build -f infrastructure/docker/api.Dockerfile --target runtime .   # image
#   docker build -f infrastructure/docker/api.Dockerfile --target test .      # run backend tests on .NET 8
ARG DOTNET_VERSION=8.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# Restore first (cached until a project file changes).
COPY Directory.Build.props Directory.Packages.props LocalAI.sln ./
COPY src/LocalAI.Core/LocalAI.Core.csproj src/LocalAI.Core/
COPY src/LocalAI.Providers/LocalAI.Providers.csproj src/LocalAI.Providers/
COPY src/LocalAI.Persistence/LocalAI.Persistence.csproj src/LocalAI.Persistence/
COPY src/LocalAI.Api/LocalAI.Api.csproj src/LocalAI.Api/
COPY tests/Directory.Build.props tests/
COPY tests/LocalAI.Core.Tests/LocalAI.Core.Tests.csproj tests/LocalAI.Core.Tests/
COPY tests/LocalAI.Providers.Tests/LocalAI.Providers.Tests.csproj tests/LocalAI.Providers.Tests/
COPY tests/LocalAI.Persistence.Tests/LocalAI.Persistence.Tests.csproj tests/LocalAI.Persistence.Tests/
COPY tests/LocalAI.Api.Tests/LocalAI.Api.Tests.csproj tests/LocalAI.Api.Tests/
RUN dotnet restore LocalAI.sln

COPY src/ src/
COPY tests/ tests/

FROM build AS test
RUN dotnet test LocalAI.sln --no-restore -c Release --logger "console;verbosity=normal"

FROM build AS publish
RUN dotnet publish src/LocalAI.Api/LocalAI.Api.csproj --no-restore -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-alpine AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_NOLOGO=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=true
COPY --from=publish /app/publish .
# SQLite database directory (mount a volume here). Owned by the non-root app user so the
# container can keep a read-only root filesystem.
RUN mkdir -p /app/data && chown $APP_UID /app/data
VOLUME /app/data
# Non-root user provided by the official .NET 8 images.
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --start-period=10s --retries=3 \
  CMD wget -q -O /dev/null http://127.0.0.1:8080/api/health || exit 1
ENTRYPOINT ["dotnet", "LocalAI.Api.dll"]
