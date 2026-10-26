# syntax=docker/dockerfile:1
# The SDK stage runs on the BUILD machine's architecture and cross-publishes for the target one
# (linux-$TARGETARCH): under buildx's QEMU an arm64 SDK stage is several times slower than a
# native one, and the output is IL plus the target's native assets either way (K-13).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Restore in its own layer, keyed on the project files only, so a source-only change reuses the
# restored packages instead of downloading them again.
COPY src/backend/Directory.Build.props ./
COPY src/backend/FrostAura.Reel.Domain/FrostAura.Reel.Domain.csproj FrostAura.Reel.Domain/
COPY src/backend/FrostAura.Reel.Application/FrostAura.Reel.Application.csproj FrostAura.Reel.Application/
COPY src/backend/FrostAura.Reel.Infrastructure/FrostAura.Reel.Infrastructure.csproj FrostAura.Reel.Infrastructure/
COPY src/backend/FrostAura.Reel.Api/FrostAura.Reel.Api.csproj FrostAura.Reel.Api/
RUN dotnet restore FrostAura.Reel.Api/FrostAura.Reel.Api.csproj -a "$TARGETARCH"

COPY src/backend/ ./
# The commit, for /health (A-17). .git is not in the build context, so the SDK cannot read it;
# CI and `make` pass it. Declared after the restore so a new commit does not invalidate that layer.
ARG GIT_SHA=
RUN dotnet publish FrostAura.Reel.Api/FrostAura.Reel.Api.csproj -c Release -a "$TARGETARCH" \
        --no-restore -o /app/publish -p:UseAppHost=false -p:SourceRevisionId="$GIT_SHA"

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# curl is needed by the compose healthcheck (the aspnet image ships without it).
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish ./

EXPOSE 5000
ENV ASPNETCORE_URLS=http://+:5000

# Never root (K-12). APP_UID is the unprivileged `app` user the .NET images ship; port 5000 needs
# no privilege, and the only path the API writes is the backup-request volume, which the backup
# sidecar keeps world-writable for exactly this reason.
USER $APP_UID

ENTRYPOINT ["dotnet", "FrostAura.Reel.Api.dll"]
