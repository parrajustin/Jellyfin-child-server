# syntax=docker/dockerfile:1.7
#
# Jellyfin child server image, built on Alpine Linux.
#
#   docker build -t jellyfin-child-server .
#
# Stages:
#   web     builds the jellyfin-web client at the tag matching this server
#   server  publishes the .NET server (framework dependent, linux-musl)
#   final   .NET ASP.NET runtime on Alpine plus ffmpeg and fonts
ARG DOTNET_VERSION=10.0
ARG NODE_VERSION=24
ARG JELLYFIN_WEB_REF=v12.1

# ---------------------------------------------------------------------------
# Web client
# ---------------------------------------------------------------------------
FROM node:${NODE_VERSION}-alpine AS web
ARG JELLYFIN_WEB_REF
ENV JELLYFIN_VERSION=${JELLYFIN_WEB_REF}
RUN apk add --no-cache git
WORKDIR /src
RUN git clone --depth 1 --branch "${JELLYFIN_WEB_REF}" https://github.com/jellyfin/jellyfin-web.git . \
 && npm ci --no-audit --no-fund \
 && npm run build:production \
 && mv dist /web

# ---------------------------------------------------------------------------
# Server
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-alpine AS server
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1
WORKDIR /src
COPY . .
RUN dotnet publish Jellyfin.Server/Jellyfin.Server.csproj \
      --configuration Release \
      --no-self-contained \
      --output /server \
      -p:DebugSymbols=false \
      -p:DebugType=none

# ---------------------------------------------------------------------------
# Final image
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-alpine

RUN apk add --no-cache \
      ca-certificates \
      ffmpeg \
      fontconfig \
      icu-data-full \
      icu-libs \
      libgcc \
      libstdc++ \
      ttf-dejavu \
      tzdata \
      wget

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    JELLYFIN_DATA_DIR=/config \
    JELLYFIN_CACHE_DIR=/cache \
    JELLYFIN_CONFIG_DIR=/config/config \
    JELLYFIN_LOG_DIR=/config/log \
    JELLYFIN_WEB_DIR=/jellyfin/jellyfin-web \
    JELLYFIN_FFMPEG=/usr/bin/ffmpeg \
    XDG_CACHE_HOME=/cache \
    HEALTHCHECK_URL=http://localhost:8096/health

COPY --from=server /server /jellyfin
COPY --from=web /web /jellyfin/jellyfin-web

RUN mkdir -p /config /cache /media \
 && chmod 777 /config /cache /media

EXPOSE 8096
VOLUME ["/config", "/cache", "/media"]

HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
  CMD wget -q -O /dev/null "${HEALTHCHECK_URL}" || exit 1

LABEL org.opencontainers.image.title="Jellyfin child server" \
      org.opencontainers.image.description="Jellyfin server that mirrors a parent Jellyfin server and caches only the media being watched" \
      org.opencontainers.image.source="https://github.com/parrajustin/Jellyfin-child-server" \
      org.opencontainers.image.licenses="GPL-2.0"

ENTRYPOINT ["dotnet", "/jellyfin/jellyfin.dll"]
