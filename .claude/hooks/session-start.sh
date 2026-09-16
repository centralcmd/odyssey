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
      # Gates npm's postinstall ALONE. Step 6's explicit `cli.js install` is deliberately not
      # blocked by it, which is what lets the browser be pinned below without a second variable.
      echo 'export PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1'
    fi
    # Aspire refuses to start behind a plain-http applicationUrl without this, and the AppHost's
    # `http` launch profile — the one CLAUDE.md recommends to dodge the Linux dev-cert banner — is
    # exactly that. Without it `dotnet run --project Odyssey.AppHost` dies at startup on an
    # OptionsValidationException, which reads as a broken AppHost rather than a missing variable.
    echo 'export ASPIRE_ALLOW_UNSECURED_TRANSPORT=true'
  } >> "${CLAUDE_ENV_FILE}"
  log "Persisted DOTNET_ROOT=${DOTNET_HOME}, PATH and the Aspire transport flag for the session."
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

# ── 4a. Pre-pull the images Testcontainers needs ─────────────────────────────────────────────────
# Odyssey.IntegrationTests pays this pull inside its fixture on a cold container (~1 minute of the
# tier's runtime, charged to whichever test happens to run first), and an image-build script cannot
# take it over: a pull needs the daemon, and the daemon is what does not survive into the session.
#
# Backgrounded and fully detached, because it is a pure optimisation — the tier works without it,
# just slower, and nothing here may delay a session that only touches the fast tiers. Output goes
# to a file rather than the hook's stdout: a child holding that pipe open would make the session
# wait for the very thing being backgrounded.
#
# The MariaDB tag is READ from the fixture rather than repeated here. A tag that silently drifts
# from the one the fixture asks for would pre-pull an image nothing uses and leave the real pull
# exactly where it was — a pessimisation that looks like a win in the log.
if docker info >/dev/null 2>&1; then
  pull_log="$( { [ -w /var/log ] && echo /var/log/odyssey-image-pull.log; } || echo "${TMPDIR:-/tmp}/odyssey-image-pull.log" )"
  fixture="${PROJECT_DIR}/Odyssey.IntegrationTests/MariaDbFixture.cs"
  mariadb_image="$(sed -n 's/.*Image = "\([^"]*\)".*/\1/p' "$fixture" 2>/dev/null | head -1)"

  # Ryuk is the resource reaper Testcontainers starts alongside the database; its tag lives inside
  # the Testcontainers package, so unlike the fixture's there is nothing in this repo to read it
  # from. A stale value costs nothing beyond the pre-pull missing — the real pull still happens at
  # test time — so it is pinned here and NOT guessed from a floating tag.
  ryuk_image="testcontainers/ryuk:0.14.0"

  if [ -n "$mariadb_image" ]; then
    (
      setsid sh -c "docker pull '$mariadb_image'; docker pull '$ryuk_image'" >"$pull_log" 2>&1 &
    ) || true
    log "Pre-pulling ${mariadb_image} and ${ryuk_image} in the background (${pull_log})."
  else
    log "NOTE: could not read the MariaDB tag from MariaDbFixture.cs; skipping the image pre-pull."
  fi
fi

# ── 4b. TLS interception vs. `docker compose --build` ─────────────────────────────────────────────
# Where the session's egress is TLS-intercepted, a build container inherits the proxy but NOT its
# CA, so `dotnet restore` inside a Dockerfile dies on NU1301 UntrustedRoot after several minutes.
# Image PULLS are unaffected (the daemon holds the CA), so the symptom reads as a NuGet outage
# rather than a trust problem — and Testcontainers keeps working, which makes it look stranger
# still. Not fixable from here: the Dockerfile bases are digest-pinned, so a locally retagged
# CA-injected base is not picked up, and injecting the CA properly means editing the Dockerfiles,
# which would bake a session-local CA into a production image. Warn and point at Aspire, which
# builds on the HOST and containerises only MariaDB.
if [ -n "${HTTPS_PROXY:-}" ] && [ -n "${SSL_CERT_FILE:-}" ] \
   && [ "${SSL_CERT_FILE}" != "/etc/ssl/certs/ca-certificates.crt" ]; then
  log "NOTE: outbound TLS is intercepted (CA: ${SSL_CERT_FILE})."
  log "      'docker compose up --build' WILL FAIL at 'dotnet restore' (NU1301 UntrustedRoot)."
  log "      Run the stack with Aspire instead:"
  log "        dotnet run --project Odyssey.AppHost --launch-profile http"
  log "      Image pulls and Testcontainers are unaffected."
  # Not a footnote: this is the command the session was just told to run, and adding `-c Release` to
  # it — which a session that has been building Release all along will do by reflex — yields a stack
  # whose API and database are healthy and whose client cannot reach either. See Odyssey.Client's
  # Program.cs: DEBUG is the compile-time signal for "dev server, talk to :5188", and a Release
  # client instead resolves same-origin /api/, which only NGINX (Compose) serves. Under Aspire that
  # path hits the SPA fallback, so every API call returns index.html and the WASM app dies parsing
  # HTML as JSON. Costs the whole browser tier, and reads as a hung sign-in rather than a mis-build.
  log "      Do NOT add '-c Release' to that command — the client resolves the API at compile time,"
  log "      so a Release client under Aspire 404s every API call to the SPA fallback."
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

# ── 6. The Playwright browser build ───────────────────────────────────────────────────────────────
# Microsoft.Playwright pins one exact Chromium revision and will use no other, so a browser baked
# into the image goes stale the moment the package is bumped (1.62.0 wants r1234; the image that
# prompted this ships r1194). Odyssey.E2ETests would still pass without this — StackFixture installs
# the browser itself and only SKIPS if that fails — but it would spend ~650 MB of download inside
# the first test run. Doing it here instead puts the browser in the cached container layer.
# Must follow the restore: the driver ships inside the resolved Microsoft.Playwright package.
#
# The export mirrors step 2's decision and is NOT redundant with it: what that step wrote is sourced
# for the SESSION, not for this process, so without setting it here the browser would land in
# Playwright's own default while the session went on looking in /opt/pw-browsers.
if [ -d /opt/pw-browsers ]; then
  export PLAYWRIGHT_BROWSERS_PATH="/opt/pw-browsers"
fi
pw_dir="$(ls -d "${HOME}"/.nuget/packages/microsoft.playwright/*/.playwright 2>/dev/null | sort -V | tail -1)"
if [ -z "${pw_dir}" ] || [ ! -x "${pw_dir}/node/linux-x64/node" ]; then
  log "NOTE: the Playwright driver is not in the NuGet cache; Odyssey.E2ETests will fetch its browser on first run."
elif "${pw_dir}/node/linux-x64/node" "${pw_dir}/package/cli.js" install chromium >/dev/null 2>&1; then
  log "Playwright Chromium ready (${PLAYWRIGHT_BROWSERS_PATH:-Playwright default})."

  # Repoint the image's convenience symlink at the build that was just installed. An image that
  # bakes browsers in also bakes `/opt/pw-browsers/chromium` pointing at ITS build (r1194), and that
  # pin does not move when the package bump above installs a newer one (r1234) alongside it. The
  # .NET tests never read the symlink — they resolve the revision themselves — so the staleness is
  # invisible until something follows the documented `executablePath` escape hatch and silently
  # launches a browser whose protocol the driver does not speak.
  #
  # Resolved from `install --dry-run` rather than by globbing for the newest directory: the revision
  # Playwright WANTS is the only correct target, and a package downgrade would make "newest" wrong.
  # The two layouts are both real — r1194 unpacks to chrome-linux/, r1234 to chrome-linux64/ — so
  # the binary is searched for rather than assumed.
  if [ -d /opt/pw-browsers ] && [ -w /opt/pw-browsers ]; then
    # The trailing `|| true` is load-bearing under `set -euo pipefail`: awk exits 0 on no match, but
    # pipefail propagates a failing cli.js, and a bare assignment would then abort the hook before
    # "Ready." — turning a cosmetic symlink into a dead session. Non-fatal, like every step above.
    chromium_dir="$("${pw_dir}/node/linux-x64/node" "${pw_dir}/package/cli.js" install --dry-run 2>/dev/null \
      | awk '/\(playwright chromium v[0-9]+\)/ { found = 1; next }
             found && /Install location:/     { print $3; exit }' || true)"
    chromium_exe=""
    for layout in chrome-linux64/chrome chrome-linux/chrome; do
      if [ -n "${chromium_dir}" ] && [ -x "${chromium_dir}/${layout}" ]; then
        chromium_exe="${chromium_dir}/${layout}"
        break
      fi
    done
    if [ -n "${chromium_exe}" ]; then
      # -n so an existing symlink is replaced rather than dereferenced into.
      ln -sfn "${chromium_exe}" /opt/pw-browsers/chromium
      log "Repointed /opt/pw-browsers/chromium -> ${chromium_exe}."
    else
      # Non-fatal: only the escape hatch is stale, and every tier resolves its own browser.
      log "NOTE: could not resolve the installed Chromium binary; /opt/pw-browsers/chromium left as-is."
    fi
  fi
else
  # Non-fatal, like every step above: the fixture runs the same install and self-skips if it fails
  # again, so this costs the browser tier at worst, never the session.
  log "WARNING: the Playwright browser install failed; Odyssey.E2ETests will retry it on first run."
fi

log "Ready."
