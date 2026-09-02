# syntax=docker/dockerfile:1
# FoundryGate.Api container image (#71). Multi-stage: .NET 10 SDK builds, ASP.NET runtime runs.
#
# Build from the REPO ROOT (the context needs the shared build props, central package versions
# and the referenced projects):
#   docker build -f src/FoundryGate.Api/Dockerfile -t foundrygate-api:local .
#   docker run --rm -p 8080:8080 -e ASPNETCORE_ENVIRONMENT=local foundrygate-api:local
#   curl -i http://localhost:8080/health          # 200 (hermetic liveness; no DB needed)
#
# Runtime contract (matches infra/modules/container-app.bicep):
#   - listens on 8080 (ASPNETCORE_HTTP_PORTS default in the aspnet image; made explicit below)
#   - runs as the image's non-root `app` user
#   - ASPNETCORE_ENVIRONMENT is NOT set here: the Container App sets it (qa | prod) together
#     with every other setting (docs reference/infrastructure "What the hosts are told").

# ---- build -------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG VERSION=0.0.1-local
ARG ASSEMBLY_VERSION=0.0.1
WORKDIR /src

# Restore first with only the project files so the NuGet layer is cached across source edits.
COPY global.json Directory.Build.props Directory.Packages.props NuGet.config .editorconfig ./
COPY src/FoundryGate.Domain/FoundryGate.Domain.csproj src/FoundryGate.Domain/
COPY src/FoundryGate.Data/FoundryGate.Data.csproj src/FoundryGate.Data/
COPY src/FoundryGate.Core/FoundryGate.Core.csproj src/FoundryGate.Core/
COPY src/FoundryGate.Api/FoundryGate.Api.csproj src/FoundryGate.Api/
RUN dotnet restore src/FoundryGate.Api/FoundryGate.Api.csproj

COPY src/FoundryGate.Domain/ src/FoundryGate.Domain/
COPY src/FoundryGate.Data/ src/FoundryGate.Data/
COPY src/FoundryGate.Core/ src/FoundryGate.Core/
COPY src/FoundryGate.Api/ src/FoundryGate.Api/

# Warnings are errors (Directory.Build.props) — the image build fails the same way CI does.
RUN dotnet publish src/FoundryGate.Api/FoundryGate.Api.csproj \
    --configuration Release \
    --output /app/publish \
    -p:UseAppHost=false \
    -p:Version=${VERSION} \
    -p:AssemblyVersion=${ASSEMBLY_VERSION} \
    -p:FileVersion=${ASSEMBLY_VERSION}

# ---- runtime -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

COPY --from=build /app/publish .

# Non-root: the official image ships an unprivileged `app` user (uid in $APP_UID).
USER $APP_UID

ENTRYPOINT ["dotnet", "FoundryGate.Api.dll"]
