#!/bin/bash
# Odyssey — SessionStart provisioning for Claude Code on the web.
#
# The remote container ships without a .NET SDK and with the Docker daemon stopped, so a session
# that does not run this spends its first minutes rediscovering both. Worse, two of the failures are
# SILENT rather than loud: Odyssey.IntegrationTests self-skips without Docker and Odyssey.E2ETests
# self-skips without a stack, so `dotnet test` reports success having run neither tier. This script
# closes that gap before the session starts.
#
# Idempotent by design — every step checks before acting, so a cached container re-runs it in
# seconds. Deliberately provisions the TOOLCHAIN only: bringing the stack up (MariaDB, migrations,
# demo seed, API, client) belongs to the `run-odyssey` skill, which needs a database whose lifetime
# is the task's, not the session's.
set -euo pipefail

# Local machines have their own toolchains; this is for the remote container alone.
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

PROJECT_DIR="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
# Overridable so the install branch below can be exercised against a throwaway directory without
# disturbing a container that already has the SDK. Sessions never set it.
DOTNET_INSTALL_DIR="${ODYSSEY_DOTNET_DIR:-/usr/local/dotnet}"
DOTNET_TOOLS_DIR="${HOME}/.dotnet/tools"

# Keep the SDK channel aligned with .github/workflows/ci.yml's dotnet-version, so a session builds
# on what CI builds on. All projects target net10.0 (CLAUDE.md).
DOTNET_CHANNEL="10.0"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

log() { printf '[odyssey-setup] %s\n' "$*"; }

# ── 1. The .NET SDK ───────────────────────────────────────────────────────────────────────────────
if [ -x "${DOTNET_INSTALL_DIR}/dotnet" ] && "${DOTNET_INSTALL_DIR}/dotnet" --list-sdks | grep -q '^10\.'; then
  log ".NET 10 SDK already present ($(${DOTNET_INSTALL_DIR}/dotnet --version))."
else
  log "Installing the .NET ${DOTNET_CHANNEL} SDK into ${DOTNET_INSTALL_DIR} ..."
  installer="$(mktemp)"
  # -L because https://dot.net/v1/dotnet-install.sh is a redirect.
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "${installer}"
  chmod +x "${installer}"
  "${installer}" --channel "${DOTNET_CHANNEL}" --install-dir "${DOTNET_INSTALL_DIR}" --no-path
  rm -f "${installer}"
  log "Installed $(${DOTNET_INSTALL_DIR}/dotnet --version)."
fi

export DOTNET_ROOT="${DOTNET_INSTALL_DIR}"
export PATH="${DOTNET_INSTALL_DIR}:${DOTNET_TOOLS_DIR}:${PATH}"

# ── 2. Persist the environment for the session ────────────────────────────────────────────────────
# DOTNET_ROOT is not optional: the SDK lives outside the default probing path, and `dotnet-ef`
# (an apphost-launched tool) fails to locate a runtime without it even when `dotnet` itself is on
# PATH — which reads as "the tool is broken" rather than "the variable is missing".
if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  {
    echo "export DOTNET_ROOT=\"${DOTNET_INSTALL_DIR}\""
    echo "export PATH=\"${DOTNET_INSTALL_DIR}:${DOTNET_TOOLS_DIR}:\${PATH}\""
    echo 'export DOTNET_CLI_TELEMETRY_OPTOUT=1'
    echo 'export DOTNET_NOLOGO=1'
    # Chromium is baked into the image; without these Playwright re-downloads it on restore.
    echo 'export PLAYWRIGHT_BROWSERS_PATH="/opt/pw-browsers"'
    echo 'export PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1'
  } >> "${CLAUDE_ENV_FILE}"
  log "Wrote DOTNET_ROOT/PATH and the Playwright variables to CLAUDE_ENV_FILE."
fi

# ── 3. dotnet-ef ──────────────────────────────────────────────────────────────────────────────────
# CLAUDE.md requires migrations be created with the dotnet tool, never by hand.
if [ -x "${DOTNET_TOOLS_DIR}/dotnet-ef" ]; then
  log "dotnet-ef already installed."
else
  log "Installing dotnet-ef ..."
  # Intentionally unpinned. The EF Core PACKAGES are 9.0.20 while the tool resolves to 10.x; the
  # mismatch is fine and is what this repo has been scaffolded with, because a 9.x tool cannot load
  # the net10.0 startup assembly it has to reflect over.
  dotnet tool install --global dotnet-ef >/dev/null
  log "Installed dotnet-ef $(dotnet-ef --version 2>/dev/null | tail -1)."
fi

# ── 4. The Docker daemon ──────────────────────────────────────────────────────────────────────────
# Odyssey.IntegrationTests uses Testcontainers-MariaDB and SELF-SKIPS when Docker is unreachable, so
# a stopped daemon costs a whole tier without reddening anything. Non-fatal: a session that only
# touches the fast tiers should still start.
if docker info >/dev/null 2>&1; then
  log "Docker daemon already running."
elif command -v dockerd >/dev/null 2>&1; then
  log "Starting the Docker daemon ..."
  (setsid dockerd >/var/log/dockerd.log 2>&1 &) || true
  for _ in $(seq 1 30); do
    if docker info >/dev/null 2>&1; then break; fi
    sleep 1
  done
  if docker info >/dev/null 2>&1; then
    log "Docker daemon up."
  else
    log "WARNING: the Docker daemon did not come up; Odyssey.IntegrationTests will self-skip. See /var/log/dockerd.log."
  fi
else
  log "WARNING: dockerd is not installed; Odyssey.IntegrationTests will self-skip."
fi

# ── 5. NuGet restore ──────────────────────────────────────────────────────────────────────────────
# Restore rather than build: it is what populates ~/.nuget/packages for the container cache, and it
# leaves the choice of Debug or Release to the session.
log "Restoring ${PROJECT_DIR}/Odyssey.sln ..."
dotnet restore "${PROJECT_DIR}/Odyssey.sln"

log "Ready."
