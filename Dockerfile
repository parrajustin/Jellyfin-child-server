# syntax=docker/dockerfile:1.7
#
# Jellyfin child server image, built on Ubuntu with jellyfin-ffmpeg.
#
#   docker build -t jellyfin-child-server .
#
# Stages:
#   web     builds the jellyfin-web client at the tag matching this server
#   server  publishes the .NET server (framework dependent)
#   final   .NET ASP.NET runtime on Ubuntu plus jellyfin-ffmpeg, VA drivers and fonts
#
# The runtime stage follows linuxserver/docker-jellyfin: the same Ubuntu release, the same
# repo.jellyfin.org apt source and the same hardware acceleration packages. It does not install
# the `jellyfin` package, because the server in this image is this fork, built from source in
# the `server` stage; only the ffmpeg build and the runtime dependencies come from the repo.
ARG DOTNET_VERSION=10.0
ARG NODE_VERSION=24
ARG JELLYFIN_WEB_REF=v12.1
# The Ubuntu release shared by the .NET images and the Jellyfin apt repository.
ARG UBUNTU_SUITE=resolute
# jellyfin-ffmpeg8 is the build Jellyfin 12.x targets. jellyfin-ffmpeg7 is also published for
# this suite if a device needs the older one.
ARG JELLYFIN_FFMPEG_PACKAGE=jellyfin-ffmpeg8

# ---------------------------------------------------------------------------
# Web client
# ---------------------------------------------------------------------------
# Pinned to the build platform: this is a webpack build producing platform independent static
# files, so running it under QEMU for an arm64 image would cost hours and change nothing.
FROM --platform=$BUILDPLATFORM node:${NODE_VERSION}-alpine AS web
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
# Also pinned to the build platform. A framework dependent publish cross compiles: naming the RID
# is enough, the SDK does not need to be running on the target architecture. So an arm64 image is
# built at native speed here and only the final stage is emulated, where it just runs apt.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-${UBUNTU_SUITE} AS server
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1
ARG TARGETARCH
WORKDIR /src
COPY . .
# The runtime identifier is explicit. A portable publish copies every platform's native libraries
# and leaves the choice to the host, which has already cost us one SIGSEGV in SkiaSharp. Naming the
# RID publishes one set and nothing else. The base is glibc now, so the RID is linux-x64 or
# linux-arm64 rather than the linux-musl-x64 the Alpine image needed.
RUN RID="linux-$(case "${TARGETARCH:-amd64}" in amd64) echo x64 ;; arm64) echo arm64 ;; *) echo "${TARGETARCH}" ;; esac)" \
 && dotnet publish Jellyfin.Server/Jellyfin.Server.csproj \
      --configuration Release \
      --runtime "${RID}" \
      --no-self-contained \
      --output /server \
      -p:DebugSymbols=false \
      -p:DebugType=none

# ---------------------------------------------------------------------------
# Final image
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-${UBUNTU_SUITE}
ARG UBUNTU_SUITE
ARG JELLYFIN_FFMPEG_PACKAGE
ARG DEBIAN_FRONTEND=noninteractive

# The Jellyfin repository signing key is kept ASCII armored. apt accepts an armored key through
# signed-by as long as the file is named .asc, which saves installing gnupg just to dearmor it.
#
# On the VA-API drivers: linuxserver installs `mesa-va-drivers`, which on this suite is only a
# virtual package provided by libgl1-mesa-dri. Naming the real package keeps apt from depending on
# that one provider staying unambiguous.
#
# intel-media-va-driver covers Intel QSV, which is what most of the small machines this image is
# aimed at actually have — but it exists only for amd64, so on arm64 it has to be left out or the
# whole apt install fails. The architecture is read with dpkg rather than TARGETARCH so this is
# right whether or not the caller passed a build arg.
RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends ca-certificates curl; \
    install -d -m 0755 /etc/apt/keyrings; \
    curl -fsSL https://repo.jellyfin.org/ubuntu/jellyfin_team.gpg.key -o /etc/apt/keyrings/jellyfin.asc; \
    chmod 0644 /etc/apt/keyrings/jellyfin.asc; \
    echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/jellyfin.asc] https://repo.jellyfin.org/ubuntu ${UBUNTU_SUITE} main" \
      > /etc/apt/sources.list.d/jellyfin.list; \
    apt-get update; \
    intel_driver=""; \
    if [ "$(dpkg --print-architecture)" = "amd64" ]; then intel_driver="intel-media-va-driver"; fi; \
    apt-get install -y --no-install-recommends \
      "${JELLYFIN_FFMPEG_PACKAGE}" \
      fonts-dejavu-core \
      fonts-noto-core \
      libfontconfig1 \
      libfreetype6 \
      libgl1-mesa-dri \
      libjemalloc2 \
      tzdata \
      ${intel_driver}; \
    fc-cache --force; \
    apt-get clean; \
    rm -rf /var/lib/apt/lists/* /tmp/* /var/tmp/*

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    JELLYFIN_DATA_DIR=/config \
    JELLYFIN_CACHE_DIR=/cache \
    JELLYFIN_CONFIG_DIR=/config/config \
    JELLYFIN_LOG_DIR=/config/log \
    JELLYFIN_WEB_DIR=/jellyfin/jellyfin-web \
    JELLYFIN_FFMPEG=/usr/lib/jellyfin-ffmpeg/ffmpeg \
    XDG_CACHE_HOME=/cache \
    HEALTHCHECK_URL=http://localhost:8096/health \
    NVIDIA_DRIVER_CAPABILITIES="compute,video,utility" \
    NVIDIA_VISIBLE_DEVICES=all \
    MALLOC_TRIM_THRESHOLD_=131072

COPY --from=server /server /jellyfin
COPY --from=web /web /jellyfin/jellyfin-web

# Seeds <config>/childserver.xml from the environment before starting, so a container can be given
# its parent URL and Cloudflare Access service token without anyone opening the dashboard, and so
# the secrets can come from a Docker or Kubernetes secret. See docker/child-server.env.example.
COPY docker/entrypoint.sh /usr/local/bin/child-server-entrypoint.sh
RUN chmod +x /usr/local/bin/child-server-entrypoint.sh

RUN mkdir -p /config /cache /media \
 && chmod 777 /config /cache /media

EXPOSE 8096
VOLUME ["/config", "/cache", "/media"]

HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
  CMD curl -fsS -o /dev/null "${HEALTHCHECK_URL}" || exit 1

LABEL org.opencontainers.image.title="Jellyfin child server" \
      org.opencontainers.image.description="Jellyfin server that mirrors a parent Jellyfin server and caches only the media being watched" \
      org.opencontainers.image.source="https://github.com/parrajustin/Jellyfin-child-server" \
      org.opencontainers.image.licenses="GPL-2.0"

ENTRYPOINT ["/usr/local/bin/child-server-entrypoint.sh"]
