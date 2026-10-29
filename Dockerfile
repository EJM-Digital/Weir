# syntax=docker/dockerfile:1
# All-in-one Weir image: the .NET server (apps/server), SQLite, ffmpeg, mkvmerge and the production
# web UI (same origin /api/v1).
#
# Build (your host's architecture):
#   docker build -t weir:local .
# Build both published architectures (needs buildx; QEMU for the non-native final stage):
#   docker buildx build --platform linux/amd64,linux/arm64 -t weir:local .
# Run:
#   docker run --rm -e WEIR_SESSION_SECRET=... -p 9347:9347 -v weir-data:/data/weir weir:local

# Base images are pinned by digest so a rebuild reproduces the same bytes; the tag comments are
# what to look up again when bumping. Digests are the multi-arch manifest list (not a single
# platform's manifest), matching how buildx resolves --platform=$BUILDPLATFORM/$TARGETARCH below.
# Re-resolve with `docker buildx imagetools inspect <image>:<tag>` where Docker is available, or
# without Docker via the registry API, e.g.:
#   curl -sI -H "Accept: application/vnd.oci.image.index.v1+json,application/vnd.docker.distribution.manifest.list.v2+json" \
#     "https://mcr.microsoft.com/v2/dotnet/runtime-deps/manifests/10.0-noble" | grep -i docker-content-digest
# (Docker Hub images such as node need a bearer token first: GET
# https://auth.docker.io/token?service=registry.docker.io&scope=repository:library/node:pull.)
# The node major here must match the root .node-version file, which every setup-node step in CI also
# reads; scripts/check-node-docker-version.mjs (repo-checks) fails the build otherwise.
FROM node:24-bookworm-slim@sha256:0e0ff40c39bc087845bfb27465a0df4ea419520094bc35842ff83dd8cbe6f9b6 AS web
WORKDIR /src/apps/web
# Only the manifest and lockfile go in before the install, so this layer (and the BuildKit cache
# mount below, which survives even when the layer cache is cold) is reused for every build that
# doesn't touch a dependency; COPY apps/web . below invalidates only the build step that follows.
COPY apps/web/package.json apps/web/package-lock.json ./
# Resilient installs in CI/buildx (registry flakes, slow links); lockfile must stay in sync with package.json.
RUN --mount=type=cache,target=/root/.npm \
  npm config set fund false \
  && npm config set audit false \
  && npm config set fetch-retries 10 \
  && npm config set fetch-retry-mintimeout 20000 \
  && npm config set fetch-retry-maxtimeout 180000 \
  && npm ci --no-audit --no-fund
COPY apps/web .
COPY scripts/dev-ports.json /src/scripts/dev-ports.json
RUN npm run build

# Runs on the build machine's own architecture (--platform=$BUILDPLATFORM): the .NET SDK cross-publishes
# a self-contained app for another runtime without emulation (it only downloads the target runtime pack),
# so this stage stays fast even when the final stage is emulated for a second architecture.
# Pinned to the exact SDK in apps/server/global.json so image builds match CI.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29 AS server-build
ARG TARGETARCH
# #804: the product version, taken from the release tag at build time (release.yml passes it; a plain
# `docker build` leaves it empty and the image reports Directory.Build.props' placeholder version
# instead, the same fallback packaging/windows/build-velopack.ps1 uses for a non-release build).
ARG WEIR_VERSION=""
WORKDIR /src
# The solution-level MSBuild files (Directory.Build.props holds the placeholder WeirVersion the WEIR_VERSION
# build-arg above overrides) and, below, only
# each project's .csproj and packages.lock.json (Directory.Build.props sets RestorePackagesWithLockFile,
# so restore expects it next to the .csproj): enough for `dotnet restore` to resolve every package,
# without the .cs source that changes on nearly every build. The restored packages live in the image layer
# itself, so a restore layer taken from the build cache always comes with the packages the --no-restore
# publish below needs. A BuildKit cache mount is deliberately not used for them: its contents are not part
# of the layer, so a cached restore layer would leave the publish step with no packages.
# .editorconfig carries the analyzer severities the build relies on (warnings are errors), so it must come too.
COPY apps/server/global.json apps/server/Directory.Build.props apps/server/Directory.Packages.props apps/server/NuGet.Config apps/server/Weir.slnx apps/server/.editorconfig apps/server/
COPY apps/server/src/Weir.Api/Weir.Api.csproj apps/server/src/Weir.Api/packages.lock.json apps/server/src/Weir.Api/
COPY apps/server/src/Weir.Core/Weir.Core.csproj apps/server/src/Weir.Core/packages.lock.json apps/server/src/Weir.Core/
COPY apps/server/src/Weir.Host/Weir.Host.csproj apps/server/src/Weir.Host/packages.lock.json apps/server/src/Weir.Host/
COPY apps/server/src/Weir.Infrastructure/Weir.Infrastructure.csproj apps/server/src/Weir.Infrastructure/packages.lock.json apps/server/src/Weir.Infrastructure/
# Restore for the target RID up front, with the same -p:SelfContained=true the publish profiles set
# (plain `-r` alone only restores the framework-dependent apphost pack, not the full self-contained
# runtime pack the profiles need), so the publish step below can run with --no-restore. Not
# --locked-mode: none of the lock files pin a RID-specific section (only "net10.0"), so a locked
# restore here would reject the RID-specific native assets (e.g. SQLitePCLRaw) it needs to add,
# the same as plain `dotnet publish -p:PublishProfile=...` already did before this change. Weir.Host's
# ProjectReferences pull Api/Core/Infrastructure into the same restore graph.
RUN case "$TARGETARCH" in \
      amd64) rid=linux-x64 ;; \
      arm64) rid=linux-arm64 ;; \
      *) echo "Dockerfile: unsupported TARGETARCH '$TARGETARCH'" >&2; exit 1 ;; \
    esac; \
    dotnet restore apps/server/src/Weir.Host -r "$rid" -p:SelfContained=true
COPY apps/server/src apps/server/src
COPY apps/web/openapi apps/web/openapi
# Publish through the checked-in per-runtime profile (Weir.Host/Properties/PublishProfiles/*.pubxml), not
# with -r/--self-contained/-p:PublishSingleFile on the command line. A command-line PublishSingleFile is a
# global property that reaches every project, and the single-file analyzer then fails Weir.Infrastructure
# with IL3000 on the Assembly.Location check that detects single-file mode on purpose. The profiles scope
# those properties to Weir.Host. Do not "simplify" this back to explicit flags.
RUN case "$TARGETARCH" in \
      amd64) profile=linux-x64 ;; \
      arm64) profile=linux-arm64 ;; \
      *) echo "Dockerfile: unsupported TARGETARCH '$TARGETARCH'" >&2; exit 1 ;; \
    esac; \
    versionArg=""; \
    if [ -n "$WEIR_VERSION" ]; then versionArg="-p:Version=$WEIR_VERSION"; fi; \
    dotnet publish apps/server/src/Weir.Host -p:PublishProfile="$profile" -p:PublishDir=/out/ --no-restore $versionArg

# A self-contained single-file publish needs only the native dependencies .NET itself uses (libc,
# OpenSSL; not ICU, because Directory.Build.props sets InvariantGlobalization), which is exactly what
# runtime-deps ships. No second copy of the managed runtime.
# .NET 10 images are Ubuntu 24.04 (noble); there is no Debian bookworm runtime-deps tag.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble@sha256:099f6f87ed745377dd27bd722f0d1a352bca71b4fddaabfd75e7c064bcaa82da
# #548: mkvtoolnix is the CLI-only package (mkvmerge, mkvinfo, mkvextract, mkvpropedit); the Qt GUI
# lives in the separate mkvtoolnix-gui package, which --no-install-recommends already keeps out. It
# lands mkvmerge on PATH at /usr/bin/mkvmerge, which is the last candidate MediaToolResolver.
# ResolveMkvmerge tries — the same way ffmpeg is found here, so the image needs no bundle directory.
# Without it the writer setting's "best" default fell back to ffmpeg for every write and the
# mkvmerge writer shipped inert.
#
# The distribution's version (noble universe ships 82.0) trails the version the Windows package pins,
# and that is accepted rather than worked around: every flag Weir.Core.Media.MkvmergeCommands builds
# — --track-order, --default-track-flag, --forced-display-flag, --no-global-tags, --no-track-tags,
# -J — has existed since v68, so both images run the same command line, and taking the tool from apt
# is what keeps it patched with the rest of the base image. GET /api/v1/system/media-tools reports
# whichever version an install actually has.
# No curl: the HEALTHCHECK below runs the app's own --healthcheck switch instead, so the image
# carries one fewer general-purpose HTTP client.
# The base image is pinned by digest, so its own packages only move when that pin does. Installing the
# distribution's pending security updates here keeps a fixed CVE in a base package (OpenSSL, libc) out of
# the image between pin bumps; the release and pull-request scans fail on any HIGH/CRITICAL with a fix.
# CI passes when Ubuntu's archive last changed (.github/actions/ubuntu-archive-stamp), so a cached copy of
# this layer is reused only while there is nothing newer to install.
ARG UBUNTU_ARCHIVE_STAMP=unset
RUN echo "Ubuntu archive: ${UBUNTU_ARCHIVE_STAMP}" \
  && apt-get update \
  && apt-get upgrade -y --no-install-recommends \
  && apt-get install -y --no-install-recommends \
    ca-certificates \
    ffmpeg \
    gosu \
    mkvtoolnix \
  && rm -rf /var/lib/apt/lists/*

WORKDIR /opt/weir
# Ubuntu base images ship an `ubuntu` user and group on uid/gid 1000; free them for `weir`.
RUN if id -u ubuntu >/dev/null 2>&1; then userdel --remove ubuntu; fi \
  && if getent group ubuntu >/dev/null 2>&1; then groupdel ubuntu; fi \
  && groupadd --system --gid 1000 weir \
  && useradd --system --uid 1000 --gid 1000 --create-home --home-dir /home/weir --shell /usr/sbin/nologin weir \
  && mkdir -p /data/weir \
  && chown weir:weir /data/weir /home/weir

# /opt/weir stays root:root (the default for a RUN/COPY with no --chown, since the build runs as
# root): the app it runs as `weir` should not be able to rewrite its own binary or web assets if a
# request handler is ever compromised. Only /data/weir (WEIR_HOME) and /home/weir are weir's.
COPY --from=server-build /out/Weir /opt/weir/Weir
RUN chmod 755 /opt/weir/Weir
COPY --from=web /src/apps/web/dist /opt/weir/web-dist
COPY docker/entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

ENV WEIR_WEB_DIST=/opt/weir/web-dist
ENV WEIR_ENV=production
# The sign-in cookie is marked HTTPS-only automatically when a request actually arrives over
# HTTPS (directly, or via a proxy listed in WEIR_TRUSTED_PROXY_IPS). Forcing it on here
# would discard the cookie on a plain-HTTP LAN install and lock the operator out for nothing,
# so the default is left as `auto`. Set WEIR_SESSION_COOKIE_SECURE=true to force it.

EXPOSE 9347

# A start that updates the database first saves a copy of it (docs/install-docker.md), which takes as long as copying the
# database does; the start period is long enough for a large one, and failures inside it do not count.
HEALTHCHECK --interval=30s --timeout=5s --start-period=5m --retries=3 \
  CMD ["/bin/sh", "-c", "/opt/weir/Weir --healthcheck --port \"${PORT:-9347}\""]

ENTRYPOINT ["/entrypoint.sh"]
