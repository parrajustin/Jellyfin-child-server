#!/bin/bash
#
# Builds and pushes the Jellyfin child server image to Docker Hub as
# xerofuzzion/jellyfin-child-server:v<N>-<arch> plus latest-<arch>, where <N> comes from
# version.json and is bumped only after every push succeeds.
#
#   ./release.sh                 # amd64 only
#   ./release.sh --arm64         # also build aarch64
#   ./release.sh --dry-run       # build locally, push nothing
#   ./release.sh --no-cache      # rebuild every layer
#   ./release.sh --skip-tests    # push without running the gates (use sparingly)
#
# Unlike the other stacks, aarch64 here is cheap: the Dockerfile pins the web and server stages
# to $BUILDPLATFORM, and a framework dependent .NET publish cross compiles from naming the RID.
# Only the final stage is emulated, and all it does is apt. No binfmt gymnastics needed for the
# build itself, though `docker run --privileged tonistiigi/binfmt --install arm64` is still
# required for the emulated final stage.
#
# Requires `docker login` for the xerofuzzion account, and jq. Nothing here logs you in, and
# --dry-run exists so the build can be exercised without credentials.
#
# ./release (no extension) is the local gate: it builds, runs every suite including the Docker
# e2e browser suite, and builds a single-arch image without pushing. This script is the
# publisher. It runs the fast gates itself (dotnet tests and the MCP server tests) but not the
# e2e suite, which needs a locally loaded image and takes far longer than a push.
#
# Runtime configuration is by environment variable; see docker/child-server.env.example and the
# "Configuration" table in docs/child-server.md. Secrets are never baked into the image.

# -E so the ERR trap also fires for failures inside functions/subshells
set -eE

cd "$(dirname "$0")"

REGISTRY="${REGISTRY:-xerofuzzion}"
IMAGE_NAME="${IMAGE_NAME:-jellyfin-child-server}"
VERSION_FILE="version.json"

# The build context is this directory: the Dockerfile publishes the .NET solution that lives
# here, and nothing outside the submodule is needed.
CONTEXT="."

BUILD_ARM64=0
DRY_RUN=0
NO_CACHE=""
SKIP_TESTS=0

while [ $# -gt 0 ]; do
  case "$1" in
    --arm64 | --all) BUILD_ARM64=1 ;;
    --dry-run) DRY_RUN=1 ;;
    --no-cache) NO_CACHE="--no-cache" ;;
    --skip-tests) SKIP_TESTS=1 ;;
    -h | --help)
      echo "Usage: $0 [--arm64] [--dry-run] [--no-cache] [--skip-tests]"
      echo
      echo "  --arm64       also build and push aarch64"
      echo "  --dry-run     build locally, push nothing, do not bump the version"
      echo "  --no-cache    rebuild every layer"
      echo "  --skip-tests  skip the dotnet and MCP gates"
      echo
      echo "Environment: REGISTRY (default xerofuzzion), IMAGE_NAME (default jellyfin-child-server)."
      exit 0
      ;;
    *) echo "error: unknown argument '$1' (see --help)" >&2; exit 1 ;;
  esac
  shift
done

# ---------------------------------------------------------------------------
# Preflight
# ---------------------------------------------------------------------------

command -v docker > /dev/null || { echo "error: docker not found on PATH" >&2; exit 1; }
command -v jq > /dev/null || { echo "error: jq not found on PATH" >&2; exit 1; }

docker buildx version > /dev/null 2>&1 \
  || { echo "error: docker buildx is required (install the buildx plugin)" >&2; exit 1; }

if [ ! -f Dockerfile ]; then
  echo "error: no Dockerfile in $(pwd)" >&2
  exit 1
fi

if [ ! -f "$VERSION_FILE" ]; then
  echo '{"version": -1}' > "$VERSION_FILE"
fi

CURRENT_VERSION=$(jq -r '.version' "$VERSION_FILE")
NEW_VERSION=$((CURRENT_VERSION + 1))
TAGNAME="v${NEW_VERSION}"

# The server version, read from SharedVersion.cs so the OCI version label cannot drift from what
# the assembly actually reports.
SERVER_VERSION=$(sed -n 's/.*AssemblyVersion("\([0-9.]*\)").*/\1/p' SharedVersion.cs | head -n 1)
SERVER_VERSION="${SERVER_VERSION:-unknown}"
COMMIT=$(git rev-parse --short HEAD 2> /dev/null || echo unknown)

# ---------------------------------------------------------------------------
# Gates
# ---------------------------------------------------------------------------

if [ "$SKIP_TESTS" -eq 1 ]; then
  echo ">> skipping the gates (--skip-tests)"
else
  if command -v dotnet > /dev/null 2>&1; then
    # Jellyfin is an ASP.NET Core application, so it needs the Microsoft.AspNetCore.App runtime and
    # its targeting pack. Distributions that split .NET into separate packages (Arch and its
    # derivatives ship dotnet-sdk, dotnet-runtime and dotnet-targeting-pack without the aspnet
    # ones) leave those out, and the build then fails with NETSDK1226 "Prune Package data not
    # found", which says nothing about what is actually missing. Check first and say so.
    if ! dotnet --list-runtimes 2> /dev/null | grep -q '^Microsoft\.AspNetCore\.App '; then
      echo "error: the Microsoft.AspNetCore.App runtime is not installed." >&2
      echo "       Jellyfin is an ASP.NET Core application and will not build without it." >&2
      echo "       Arch and derivatives:  sudo pacman -S aspnet-runtime aspnet-targeting-pack" >&2
      echo "       Debian and Ubuntu:     sudo apt install aspnetcore-runtime-10.0" >&2
      exit 1
    fi

    # The runtime can be present while the reference assemblies are not, which fails the same way.
    sdk_dir=$(dotnet --list-sdks 2> /dev/null | tail -n 1 | sed -n 's/.*\[\(.*\)\]/\1/p')
    if [ -n "$sdk_dir" ] && [ -d "$(dirname "$sdk_dir")/packs" ] \
      && [ ! -d "$(dirname "$sdk_dir")/packs/Microsoft.AspNetCore.App.Ref" ]; then
      echo "error: the Microsoft.AspNetCore.App targeting pack is missing from $(dirname "$sdk_dir")/packs." >&2
      echo "       The runtime alone is not enough to build; the reference assemblies are needed too." >&2
      echo "       Arch and derivatives:  sudo pacman -S aspnet-targeting-pack" >&2
      exit 1
    fi

    echo ">> dotnet build and test"
    dotnet build Jellyfin.sln --configuration Release \
      || { echo "error: the solution does not build" >&2; exit 1; }
    dotnet test Jellyfin.sln --configuration Release --no-build --verbosity minimal \
      || { echo "error: unit or integration tests failed" >&2; exit 1; }
  else
    echo "error: dotnet not found; install the SDK or pass --skip-tests" >&2
    exit 1
  fi

  if command -v node > /dev/null 2>&1; then
    echo ">> MCP server tests"
    (cd tools/jellyfin-mcp && npm ci --no-audit --no-fund && npm test) \
      || { echo "error: the MCP server tests failed" >&2; exit 1; }
  else
    echo "error: node not found; install it or pass --skip-tests" >&2
    exit 1
  fi

  # The entrypoint generates the config the server reads, so a break here produces a container
  # that comes up ignoring its environment. Cheap: bash and python3 only.
  echo ">> entrypoint environment tests"
  bash tests/env/entrypoint.test.sh \
    || { echo "error: the entrypoint environment tests failed" >&2; exit 1; }
fi

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------

build_and_push() {
  local platform="$1" arch="$2"
  local image="${REGISTRY}/${IMAGE_NAME}"

  echo ">> ${image}:${TAGNAME}-${arch} (${platform})"
  if [ "$DRY_RUN" -eq 1 ]; then
    # type=docker loads into the local daemon instead of pushing. buildx refuses --platform with
    # type=docker when it differs from the host, so a dry run only exercises the native
    # architecture.
    docker buildx build ${NO_CACHE} \
      -f Dockerfile \
      --label "org.opencontainers.image.version=${SERVER_VERSION}" \
      --label "org.opencontainers.image.revision=${COMMIT}" \
      -t "${image}:${TAGNAME}-${arch}" -t "${image}:latest-${arch}" \
      --output type=docker "$CONTEXT"
  else
    docker buildx build ${NO_CACHE} \
      -f Dockerfile \
      --platform "$platform" \
      --label "org.opencontainers.image.version=${SERVER_VERSION}" \
      --label "org.opencontainers.image.revision=${COMMIT}" \
      -t "${image}:${TAGNAME}-${arch}" -t "${image}:latest-${arch}" \
      --output type=image,push=true,compression=zstd,force-compression=true,compression-level=3 \
      "$CONTEXT"
  fi
}

# The point of the image is the patched ffmpeg, so prove it is the one that ended up in it. Only
# possible for an image in the local daemon, so this checks the native build before any push.
verify_ffmpeg() {
  local image="$1"
  echo ">> checking ${image} ships jellyfin-ffmpeg"
  local banner
  banner=$(docker run --rm --entrypoint /usr/lib/jellyfin-ffmpeg/ffmpeg "$image" -version 2> /dev/null | head -n 1) \
    || { echo "error: jellyfin-ffmpeg is not at /usr/lib/jellyfin-ffmpeg/ffmpeg in ${image}" >&2; exit 1; }
  if ! grep -qi jellyfin <<< "$banner"; then
    echo "error: the ffmpeg in ${image} is not a Jellyfin build: ${banner}" >&2
    exit 1
  fi
  echo "   ${banner}"

  local configured
  configured=$(docker run --rm --entrypoint sh "$image" -c 'printf %s "$JELLYFIN_FFMPEG"')
  if [ "$configured" != "/usr/lib/jellyfin-ffmpeg/ffmpeg" ]; then
    echo "error: JELLYFIN_FFMPEG in ${image} is '${configured}', not the jellyfin-ffmpeg build" >&2
    exit 1
  fi
  echo "   JELLYFIN_FFMPEG=${configured}"
}

NATIVE_ARCH=$(docker version --format '{{.Server.Arch}}' 2> /dev/null || echo amd64)

build_and_push linux/amd64 x86_64
if [ "$DRY_RUN" -eq 1 ] && [ "$NATIVE_ARCH" = "amd64" ]; then
  verify_ffmpeg "${REGISTRY}/${IMAGE_NAME}:${TAGNAME}-x86_64"
fi

if [ "$BUILD_ARM64" -eq 1 ]; then
  build_and_push linux/arm64 aarch64
  if [ "$DRY_RUN" -eq 1 ] && [ "$NATIVE_ARCH" = "arm64" ]; then
    verify_ffmpeg "${REGISTRY}/${IMAGE_NAME}:${TAGNAME}-aarch64"
  fi
else
  echo "Skipping aarch64 — pass --arm64 to build it."
fi

if [ "$DRY_RUN" -eq 1 ]; then
  echo
  echo "Dry run: nothing was pushed and ${VERSION_FILE} was not bumped."
  echo "The images are in the local daemon."
  exit 0
fi

# version.json is bumped only after every push has succeeded, so a failed release does not burn a
# version number.
jq --arg v "$NEW_VERSION" '.version = ($v | tonumber)' "$VERSION_FILE" > version.tmp.json \
  && mv version.tmp.json "$VERSION_FILE"

echo
echo "pushed ${REGISTRY}/${IMAGE_NAME}:${TAGNAME}-x86_64 (and latest-x86_64)"
if [ "$BUILD_ARM64" -eq 1 ]; then
  echo "pushed ${REGISTRY}/${IMAGE_NAME}:${TAGNAME}-aarch64 (and latest-aarch64)"
fi
echo "version.json updated to version ${NEW_VERSION}"
echo
echo "Run it with an env file (see docker/child-server.env.example):"
echo "  docker run --env-file child-server.env -v /srv/child/config:/config \\"
echo "    -v /srv/child/cache:/cache --device /dev/dri:/dev/dri \\"
echo "    -p 8096:8096 ${REGISTRY}/${IMAGE_NAME}:${TAGNAME}-x86_64"
