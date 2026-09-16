#!/bin/bash
# Odyssey — SessionStart provisioning for Claude Code on the web.
#
# A remote container may arrive without a .NET SDK and with the Docker daemon stopped, so a session
# that does not run this spends its first minutes rediscovering both. The daemon matters most
# because its absence is SILENT: Odyssey.IntegrationTests self-skips when Docker is unreachable, so
# `dotnet test` reports success having never run that tier.
#
# **Everything here DETECTS rather than assumes.** The same repo is opened from more than one kind
# of environment — a stock image, one whose environment configuration already baked the SDK in
# (see docs/claude-code-environment.md), a different base OS, CI — and a hook that hardcodes one
# layout is worse than no hook: it would re-download an SDK that is already installed and then put
# its own copy first on PATH. So each step asks the machine what it has before changing anything,
# and every step is idempotent.
#
# Deliberately provisions the TOOLCHAIN only: bringing the stack up (MariaDB, migrations, demo
# seed, API, client) belongs to the `run-odyssey` skill, which needs a database whose lifetime is
# the task's, not the session's.
set -euo pipefail

# Local machines have their own toolchains and their own package managers; this is for the remote
# container alone. Never apt-install on somebody's laptop.
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

PROJECT_DIR="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"

# Keep the SDK major aligned with .github/workflows/ci.yml's dotnet-version, so a session builds on
# what CI builds on. All projects target net10.0 (CLAUDE.md).
DOTNET_MAJOR="10"
DOTNET_CHANNEL="10.0"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

log() { printf '[odyssey-setup] %s\n' "$*"; }

# Elevation, if this environment needs and offers it. Root is the common case in a container.
if [ "$(id -u)" -eq 0 ]; then
  SUDO=""
elif command -v sudo >/dev/null 2>&1 && sudo -n true 2>/dev/null; then
  SUDO="sudo"
else
  SUDO="none"
fi

# ── 1. The .NET SDK ───────────────────────────────────────────────────────────────────────────────
# Three ways a usable SDK can already be here, in descending order of preference. Finding one is by
# far the most likely outcome, so it is the fast path.
has_sdk() { "$1" --list-sdks 2>/dev/null | grep -q "^${DOTNET_MAJOR}\."; }

dotnet_bin=""

# (a) Already on PATH — an apt/dnf package, an environment-configuration install, a CI setup step.
if command -v dotnet >/dev/null 2>&1 && has_sdk dotnet; then
  dotnet_bin="$(command -v dotnet)"
  log ".NET $("$dotnet_bin" --version) already on PATH."
else
  # (b) Installed but not on PATH. These are the standard locations across the layouts this repo
  #     has actually been built in; a tarball install puts nothing on PATH by itself.
  for candidate in /usr/local/dotnet /usr/lib/dotnet /usr/share/dotnet "$HOME/.dotnet"; do
    if [ -x "${candidate}/dotnet" ] && has_sdk "${candidate}/dotnet"; then
      dotnet_bin="${candidate}/dotnet"
      export PATH="${candidate}:${PATH}"
      log ".NET $("$dotnet_bin" --version) found at ${candidate}."
      break
    fi
  done
fi

if [ -z "$dotnet_bin" ]; then
  # (c) Genuinely absent. Prefer the distribution's own package — it is signed, cached in the image
  #     layer, and installs a layout whose `dotnet` self-resolves a runtime. Fall back to the
  #     official tarball only when that is unavailable.
  installed=0
  if [ "$SUDO" != "none" ] && command -v apt-get >/dev/null 2>&1; then
    # `apt-get update` is not optional even on a fresh image: the shipped index pins an exact
    # version, and Ubuntu supersedes the dotnet packages often enough that a stale index fails with
    # a bare `404 Not Found` on the .deb rather than anything naming the real problem.
    log "Installing dotnet-sdk-${DOTNET_CHANNEL} via apt ..."
    if DEBIAN_FRONTEND=noninteractive $SUDO apt-get update -qq \
       && DEBIAN_FRONTEND=noninteractive $SUDO apt-get install -y -qq --no-install-recommends \
            "dotnet-sdk-${DOTNET_CHANNEL}" \
       && command -v dotnet >/dev/null 2>&1 && has_sdk dotnet; then
      dotnet_bin="$(command -v dotnet)"
      installed=1
      log "Installed .NET $("$dotnet_bin" --version) from apt."
    else
      log "apt could not provide dotnet-sdk-${DOTNET_CHANNEL}; falling back to the official installer."
    fi
  fi

  if [ "$installed" -eq 0 ]; then
    # A writable system-wide location if we have one, otherwise the user's own — never assume root.
    if [ "$(id -u)" -eq 0 ] || [ -w /usr/local ]; then
      install_dir="/usr/local/dotnet"
    else
      install_dir="${HOME}/.dotnet"
    fi

    log "Installing the .NET ${DOTNET_CHANNEL} SDK into ${install_dir} ..."
    installer="$(mktemp)"
    # Downloaded and then run, rather than piped into a shell: a truncated transfer cannot execute
    # as a partial script. -L because dot.net/v1/dotnet-install.sh is a redirect.
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$installer"
    chmod +x "$installer"
    "$installer" --channel "${DOTNET_CHANNEL}" --install-dir "${install_dir}" --no-path
    rm -f "$installer"

    dotnet_bin="${install_dir}/dotnet"
    export PATH="${install_dir}:${PATH}"
    log "Installed .NET $("$dotnet_bin" --version)."
  fi
fi

# DOTNET_ROOT is derived from whichever SDK won above, never hardcoded. A tarball layout needs it:
# `dotnet-ef` is apphost-launched and cannot find a runtime without it, which presents as "the tool
# is broken" rather than "a variable is missing". A distro-packaged layout resolves without it, but
# setting it to that same directory is correct there too, so one line covers both.
DOTNET_HOME="$(cd "$(dirname "$(readlink -f "$dotnet_bin")")" && pwd)"
export DOTNET_ROOT="${DOTNET_HOME}"
export PATH="${DOTNET_HOME}:${HOME}/.dotnet/tools:${PATH}"

# ── 2. Persist the environment for the session ────────────────────────────────────────────────────
if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  {
    echo "export DOTNET_ROOT=\"${DOTNET_HOME}\""
    echo "export PATH=\"${DOTNET_HOME}:${HOME}/.dotnet/tools:\${PATH}\""
    echo 'export DOTNET_CLI_TELEMETRY_OPTOUT=1'
    echo 'export DOTNET_NOLOGO=1'
    # Only when the image actually bakes the browsers in. Exporting a path that does not exist
    # would make Playwright look in an empty directory instead of downloading Chromium.
    if [ -d /opt/pw-browsers ]; then
      echo 'export PLAYWRIGHT_BROWSERS_PATH="/opt/pw-browsers"'
      echo 'export PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1'
    fi
  } >> "${CLAUDE_ENV_FILE}"
  log "Persisted DOTNET_ROOT=${DOTNET_HOME} and PATH for the session."
fi

# ── 3. dotnet-ef ──────────────────────────────────────────────────────────────────────────────────
# CLAUDE.md requires migrations be created with the dotnet tool, never by hand.
if command -v dotnet-ef >/dev/null 2>&1; then
  log "dotnet-ef already installed."
else
  # Intentionally unpinned. The EF Core PACKAGES are 9.0.x while the tool resolves to 10.x; the
  # mismatch is what this repo has been scaffolded with, because a 9.x tool cannot load the net10.0
  # startup assembly it has to reflect over.
  # Non-fatal. Under `set -e` a transient NuGet failure here would abort the hook before the Docker
  # daemon was started, turning "no migrations tool" into "no integration tests either".
  log "Installing dotnet-ef ..."
  if dotnet tool install --global dotnet-ef >/dev/null 2>&1; then
    log "Installed dotnet-ef $(dotnet-ef --version 2>/dev/null | tail -1)."
  else
    log "WARNING: could not install dotnet-ef; migrations cannot be scaffolded this session."
  fi
fi

# ── 4. The Docker daemon ──────────────────────────────────────────────────────────────────────────
# Non-fatal throughout: a session that only touches the fast tiers should still start. This is the
# one step an environment configuration CANNOT do for us — a daemon started at image-build time does
# not survive into the session container.
if docker info >/dev/null 2>&1; then
  log "Docker daemon already running."
elif ! command -v dockerd >/dev/null 2>&1; then
  log "NOTE: dockerd is not installed; Odyssey.IntegrationTests will self-skip."
elif [ "$SUDO" = "none" ]; then
  log "NOTE: no privileges to start dockerd; Odyssey.IntegrationTests will self-skip."
else
  log "Starting the Docker daemon ..."
  daemon_log="$( { [ -w /var/log ] && echo /var/log/dockerd.log; } || echo "${TMPDIR:-/tmp}/dockerd.log" )"
  (setsid $SUDO dockerd >"$daemon_log" 2>&1 &) || true
  for _ in $(seq 1 30); do
    docker info >/dev/null 2>&1 && break
    sleep 1
  done
  if docker info >/dev/null 2>&1; then
    log "Docker daemon up."
  else
    log "WARNING: the daemon did not come up; Odyssey.IntegrationTests will self-skip. See ${daemon_log}."
  fi
fi

# ── 5. NuGet restore ──────────────────────────────────────────────────────────────────────────────
# Restore rather than build: it is what populates ~/.nuget/packages, and it leaves the choice of
# Debug or Release to the session.
if [ -f "${PROJECT_DIR}/Odyssey.sln" ]; then
  # Also non-fatal, and for the same reason: a failed restore is worth reporting, but a session that
  # cannot start at all is strictly harder to diagnose from than one that starts and says why.
  log "Restoring ${PROJECT_DIR}/Odyssey.sln ..."
  if ! dotnet restore "${PROJECT_DIR}/Odyssey.sln"; then
    log "WARNING: restore failed — the first build of this session will retry it."
  fi
else
  log "NOTE: no Odyssey.sln at ${PROJECT_DIR}; skipping restore."
fi

log "Ready."
