# syntax=docker/dockerfile:1
# FoundryGate.Api container image (#71). Multi-stage: .NET 10 SDK builds, ASP.NET runtime runs.
#
# Build from the REPO ROOT (the context needs the shared build props, central package versions
# and the referenced projects):
#   docker build -f src/FoundryGate.Api/Dockerfile -t foundrygate-api:local .
#   docker run --rm -p 8080:8080 -e ASPNETCORE_ENVIRONMENT=local \
#     -e 'ConnectionStrings__FoundryGate=Server=localhost;Database=FoundryGate;Integrated Security=true;TrustServerCertificate=True' \
#     -e AzureAd__TenantId=00000000-0000-0000-0000-000000000000 \
#     -e AzureAd__ClientId=00000000-0000-0000-0000-000000000000 \
#     -e AzureAd__Audience=api://00000000-0000-0000-0000-000000000000 \
#     -e KeyProtection__Provider=DataProtection \
#     -e AzureAd__Instance=https://login.microsoftonline.com/ \
#     foundrygate-api:local
#   curl -i http://localhost:8080/health          # 200 (hermetic liveness; no DB needed)
#
# Those settings are required by Program.cs's startup validation and MUST come from the
# environment — the image ships no appsettings.local.json (.dockerignore, enforced by
# .github/actions/build-api-image). On Container Apps they come from
# infra/modules/control-plane.bicep; /health itself is hermetic, so placeholders are fine here.
#
# Runtime contract (matches infra/modules/container-app.bicep):
#   - listens on 8080 (ASPNETCORE_HTTP_PORTS default in the aspnet image; made explicit below)
#   - runs as the image's non-root `app` user
#   - ASPNETCORE_ENVIRONMENT is NOT set here: the Container App sets it (qa | prod) together
#     with every other setting (docs reference/infrastructure "What the hosts are told").
#
# BASE IMAGES ARE PINNED BY DIGEST (#157). A floating `:10.0` tag means two builds of the same
# commit can produce different images — the thing a container image is supposed to make
# impossible. The tag is kept in the comment beside each digest so a human can still see which
# one it is. Digests are the multi-arch manifest LIST digests, so linux/amd64 and linux/arm64
# builds both resolve correctly from the same reference.
#
# TO REFRESH (do it deliberately, in its own PR, when a .NET patch ships):
#   docker buildx imagetools inspect mcr.microsoft.com/dotnet/sdk:10.0    --format '{{.Manifest.Digest}}'
#   docker buildx imagetools inspect mcr.microsoft.com/dotnet/aspnet:10.0 --format '{{.Manifest.Digest}}'
# paste both below, and let api-deploy.yml's PR image build prove /health still answers 200.
# Automating that refresh is #212 (MCR is not Docker Hub, so Renovate/Dependabot need the
# docker datasource pointed at mcr.microsoft.com, or a regex manager keyed on the tag comments
# above each FROM — which is why those comments are there).

# ---- build -------------------------------------------------------------------------------
# mcr.microsoft.com/dotnet/sdk:10.0 (resolved 2026-09-02)
FROM mcr.microsoft.com/dotnet/sdk@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build
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
# mcr.microsoft.com/dotnet/aspnet:10.0 (resolved 2026-09-02)
FROM mcr.microsoft.com/dotnet/aspnet@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

COPY --from=build /app/publish .

# Non-root: the official image ships an unprivileged `app` user (uid in $APP_UID).
USER $APP_UID

ENTRYPOINT ["dotnet", "FoundryGate.Api.dll"]
