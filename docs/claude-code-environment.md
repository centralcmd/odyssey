# The Claude Code environment

Odyssey is worked on from Claude Code sessions that run in an ephemeral remote container. Two
mechanisms provision it, and **which one a given step belongs to is decided by whether the step
survives being cached into an image**.

| | Environment configuration | `SessionStart` hook |
|---|---|---|
| Runs | once, at image build | at the start of every session |
| Defined in | the Claude Code environment settings (not this repo) | [`.claude/hooks/session-start.sh`](../.claude/hooks/session-start.sh) |
| Good for | packages, SDKs, a warm NuGet cache — filesystem state | anything needing a **running process** |
| Cannot do | start a daemon: a build-time process does not survive into the session container | nothing, but it pays its cost on every session |

The split matters most for **Docker**. `Odyssey.IntegrationTests` uses Testcontainers and
**self-skips** when the daemon is unreachable, so a stopped daemon costs a whole test tier while
`dotnet test` still reports success. That is a silent failure, not a red one — which is why starting
the daemon is the hook's job and cannot be delegated to the image.

The same boundary places the Testcontainers **image pre-pull**. A cold container pays ~1 minute of
`docker pull` inside the fixture, charged to whichever test runs first, and no image-build step can
take that over: a pull needs the daemon. So the hook does it, backgrounded and fully detached —
the tier works without it, just slower, and nothing may delay a session that only touches the fast
tiers. The hook **reads** the MariaDB tag out of `Odyssey.IntegrationTests/MariaDbFixture.cs`
rather than repeating it; a tag that drifted from the fixture's would pre-pull an image nothing
uses and leave the real pull exactly where it was — a pessimisation that looks like a win in the
log. Ryuk, the resource reaper, is the exception: its tag lives inside the Testcontainers package,
so there is nothing in this repo to read it from and it is pinned in the hook.

## The hook detects; it does not assume

The same repository is opened from more than one kind of environment: a stock image, one whose
environment configuration already installed the SDK, a different base OS, or CI. A hook that
hardcoded one layout would be **worse than no hook** — it would re-download an SDK that is already
present and then put its own copy first on `PATH`.

So the hook looks for a usable SDK in three descending preferences before installing anything: on
`PATH`, then in the standard install locations (`/usr/local/dotnet`, `/usr/lib/dotnet`,
`/usr/share/dotnet`, `~/.dotnet`), and only then installs — preferring the distribution package over
the upstream tarball. `DOTNET_ROOT` is **derived** from whichever SDK won, by resolving the `dotnet`
binary's real path, rather than being a constant.

That derivation is load-bearing rather than tidy. A distro-packaged SDK symlinks `/usr/bin/dotnet`
into `/usr/lib/dotnet` and resolves a runtime by itself; a tarball install does not, and without
`DOTNET_ROOT` an apphost-launched tool like `dotnet-ef` fails in a way that reads as *the tool is
broken* rather than *a variable is missing*. One derived value is correct in both layouts.

Every step is idempotent and non-interactive, the hook is gated on `CLAUDE_CODE_REMOTE` so it never
touches a developer's own machine, and the steps after the SDK are **non-fatal**: a transient NuGet
failure must not abort the hook before the Docker daemon has been started.

## What the image bakes in can go stale, and the hook re-derives it

`Microsoft.Playwright` pins one exact Chromium revision and will use no other, so a browser baked
into the image goes stale the moment the package is bumped — the image that prompted this shipped
`r1194` while the pinned 1.62.0 wants `r1234`, which cost every session a ~650 MB download
(the browser plus the headless shell) before `Odyssey.E2ETests` could run, and made session start
depend on reaching Playwright's CDN.

The environment configuration therefore installs the **wanted** revision at build time and prunes
the superseded one, and it does so by asking the driver — `install --dry-run chromium` names every
component it needs, so the keep-list is complete by construction rather than by a maintained list
of prefixes. Two things make that safe to automate. The driver ships inside the NuGet package at a
stable path (`.playwright/{node,package}`), so the step needs the warm cache from the step before
it and no compile at all. And the prune is guarded on every wanted component being present *after*
the install: if the install half-failed, that keep-list would be the only thing standing between
the prune and deleting the last working browser.

Baking the right build does not retire the hook's copy of this logic — it makes it a no-op in the
common case and a rescue in the rest. The package can be bumped after an image is built, and the
image can be rebuilt from a base that ships something else again. Installing the right build is
also only half of it: the image leaves `/opt/pw-browsers/chromium`, the convenience symlink the
`executablePath` escape hatch points at, aimed at **its** build, and that pin does not move on its
own.

Nothing in the .NET test tiers reads the symlink — each resolves its own revision — so a stale one is
invisible until something follows that documented escape hatch and silently launches a browser whose
protocol the driver does not speak. The hook therefore repoints it, resolving the target from
`install --dry-run` rather than by globbing for the newest directory: the revision Playwright *wants*
is the only correct answer, and a package **downgrade** would make "newest" the wrong one. The
executable is searched for rather than assumed, because the two layouts are both real — `r1194`
unpacks to `chrome-linux/`, `r1234` to `chrome-linux64/`.

The same staleness reaches Node consumers, differently. The hook exports
`PLAYWRIGHT_BROWSERS_PATH=/opt/pw-browsers`, which redirects the `playwright` npm package away from
`~/.cache/ms-playwright` — so the `run-odyssey` driver's pin has to equal the `Microsoft.Playwright`
version in `Directory.Packages.props`, or it looks for a build that directory will never hold. Both
are `1.62.0`; keep them in lockstep.

## The environment-configuration script

This is not run from the repository — paste it into the environment's setup script. It is recorded
here so the two halves stay legible as one design.

```bash
#!/usr/bin/env bash
#
# Odyssey — Claude Code environment setup (Ubuntu 24.04).
#
# Runs as root at environment BUILD time, so its results are baked into the cached image. Everything
# here is a filesystem change that survives; anything needing a running process (the Docker daemon)
# does NOT belong here — see the note at the end.
#
# Idempotent and non-interactive: safe to re-run, never prompts.
set -euo pipefail

export DEBIAN_FRONTEND=noninteractive
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# One place to state the band. Every project targets net10.0, and this must stay aligned with
# .github/workflows/ci.yml's dotnet-version so an image builds what CI builds.
DOTNET_MAJOR="10"
DOTNET_CHANNEL="10.0"

# Where the build and the smoke test write their output. A failure here is reported at image-build
# time, but it is read later — from a session — so it has to land somewhere that survives.
SETUP_LOG="/var/log/odyssey-env-setup.log"

log()  { printf '\n[odyssey-env] %s\n' "$*"; }
ok()   { printf '[odyssey-env]   ok: %s\n' "$*"; }
warn() { printf '[odyssey-env]   WARNING: %s\n' "$*"; }

# The clone location varies by how the environment is configured; take the first that has the
# solution in it rather than hardcoding one.
REPO_DIR=""
for candidate in "${CLAUDE_PROJECT_DIR:-}" /home/user/odyssey "$PWD"; do
  [ -n "$candidate" ] && [ -f "${candidate}/Odyssey.sln" ] && { REPO_DIR="$candidate"; break; }
done

# `apt-get update` is NOT optional even on a freshly built image: the shipped index pins an exact
# version, and Ubuntu supersedes packages often enough that a stale index fails with a bare
# `404 Not Found` on the .deb rather than anything naming the real problem. Run it at most once.
apt_updated=0
apt_install() {
  if [ "$apt_updated" -eq 0 ]; then
    apt-get update -qq
    apt_updated=1
  fi
  apt-get install -y -qq --no-install-recommends "$@"
}

# ── 1. The .NET 10 SDK ────────────────────────────────────────────────────────────────────────────
# Every project targets net10.0 and Directory.Build.props sets TreatWarningsAsErrors, so an older
# SDK does not "mostly work" — it cannot restore at all.
log "1/6  .NET ${DOTNET_MAJOR} SDK"

if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks | grep -q "^${DOTNET_MAJOR}\."; then
  ok ".NET $(dotnet --version) already present"
else
  apt_install "dotnet-sdk-${DOTNET_CHANNEL}"

  # The apt package symlinks /usr/bin/dotnet into /usr/lib/dotnet, so the apphost resolves a runtime
  # on its own. That is why this needs no DOTNET_ROOT, unlike a tarball install into /usr/local —
  # where its absence makes `dotnet-ef` fail in a way that reads as a broken tool.
  ok "Installed .NET $(dotnet --version) at $(dirname "$(readlink -f "$(command -v dotnet)")")"
fi

# ── 2. dotnet-ef ──────────────────────────────────────────────────────────────────────────────────
# CLAUDE.md: migrations are always created and applied with the dotnet tool, never hand-written.
log "2/6  dotnet-ef"

TOOLS_DIR="${HOME}/.dotnet/tools"
export PATH="${TOOLS_DIR}:${PATH}"

if command -v dotnet-ef >/dev/null 2>&1; then
  ok "dotnet-ef $(dotnet-ef --version 2>/dev/null | tail -1) already present"
else
  # Pinned to the SDK's own major, not to the EF Core PACKAGE version. Those are deliberately
  # different: the packages are 9.0.x (Pomelo 9.0.0 floors them there) while the tool must be 10.x,
  # because a 9.x tool cannot load the net10.0 startup assembly it has to reflect over.
  #
  # The band matters in the other direction too. Left unpinned, this installs whatever is newest —
  # so the first .NET 11 release would put an 11.x tool, whose apphost needs a net11.0 runtime, onto
  # a machine that has only 10.x. That failure reads as "dotnet-ef is broken", not as a version
  # mismatch. The fallback keeps a band that has not been published yet from aborting the build.
  dotnet tool install --global dotnet-ef --version "${DOTNET_MAJOR}.*" \
    || dotnet tool install --global dotnet-ef
  ok "Installed dotnet-ef $(dotnet-ef --version 2>/dev/null | tail -1)"
fi

# Global tools live outside the default PATH for non-login shells, so pin it for every future shell.
if [ -d /etc/profile.d ] && [ ! -f /etc/profile.d/dotnet-tools.sh ]; then
  printf 'export PATH="$HOME/.dotnet/tools:$PATH"\n' > /etc/profile.d/dotnet-tools.sh
  chmod 0644 /etc/profile.d/dotnet-tools.sh
  ok "Pinned the global-tools directory onto PATH via /etc/profile.d"
fi

# ── 3. The MariaDB client ─────────────────────────────────────────────────────────────────────────
# The one tool whose absence has no workaround from inside a session. Odyssey's database is only
# ever reachable as a container (Compose and Aspire both publish it on host port 3307), and without
# a client the only way in is `docker exec` into that container — which does not exist while the
# stack is down, and cannot be pointed at a Testcontainers instance on an ephemeral port at all.
# The `reset-environment` skill and any hand diagnosis of a migration both want this.
log "3/6  MariaDB client"

if command -v mariadb >/dev/null 2>&1; then
  ok "$(mariadb --version)"
else
  # Ubuntu 24.04 ships the 10.11 client; the server this talks to is 11.4. That pairing is fine —
  # the wire protocol is stable across both — and it is why this does NOT add MariaDB's own apt
  # repository to match versions exactly: an unsigned third-party source is a far worse trade than a
  # client one minor behind. The package provides `mysql` as well, so either name works.
  apt_install mariadb-client
  ok "Installed $(mariadb --version)"
fi

# ── 4. Warm the NuGet cache ───────────────────────────────────────────────────────────────────────
# The single most valuable thing to bake into the image: it turns the first build of a session from
# minutes into seconds, and it is pure filesystem state, so the cache survives.
#
# Note what does NOT survive: the per-project obj/ (project.assets.json and friends) is rebuilt on
# the session's first restore regardless, so this warms ~/.nuget/packages and nothing more. That is
# still the whole win — a restore against a warm cache is seconds.
log "4/6  NuGet restore"

if [ -n "$REPO_DIR" ]; then
  dotnet restore "${REPO_DIR}/Odyssey.sln"
  ok "Restored ${REPO_DIR}/Odyssey.sln"
else
  warn "No Odyssey.sln found — skipping restore. If the repo is cloned AFTER this script runs, that"
  warn "is expected; the SessionStart hook restores instead and only the cache warm-up is lost."
fi

# ── 5. Playwright's Chromium, at the revision the pinned package wants ────────────────────────────
# `Microsoft.Playwright` pins one exact Chromium revision and will use no other, so "a browser is
# baked in" is not the same as "the browser tiers need no download". An image carrying a superseded
# build costs every session a ~650 MB download (chromium + the headless shell) before
# Odyssey.E2ETests can run, and makes session start depend on reaching Playwright's CDN.
#
# This runs after the restore because it uses the driver out of the NuGet cache rather than a build
# output: the package lays down .playwright/{node,package} at a stable path, so no compile is needed
# to ask it what it wants or to make it fetch it.
log "5/6  Playwright Chromium"

export PLAYWRIGHT_BROWSERS_PATH="${PLAYWRIGHT_BROWSERS_PATH:-/opt/pw-browsers}"
# Whatever the base image set this to, an explicit install must not be skipped.
unset PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD || true

PW_DRIVER="$(find "${HOME}/.nuget/packages/microsoft.playwright" -maxdepth 2 -type d -name .playwright 2>/dev/null | sort -V | tail -1)"

if [ -z "$PW_DRIVER" ] || [ ! -x "${PW_DRIVER}/node/linux-x64/node" ]; then
  warn "No Playwright driver in the NuGet cache — Odyssey.E2ETests will fetch its browser on first"
  warn "run. Expected when the restore above was skipped."
else
  PW_NODE="${PW_DRIVER}/node/linux-x64/node"
  PW_CLI="${PW_DRIVER}/package/cli.js"

  # Ask the driver, never a glob over /opt/pw-browsers. The revision Playwright WANTS is the only
  # correct answer, and a package downgrade would make "the newest directory" the wrong one. The
  # dry-run names every component it needs — the browser, the headless shell and ffmpeg — so the
  # keep-list below is complete by construction rather than by a maintained list of prefixes.
  PW_KEEP="$("$PW_NODE" "$PW_CLI" install --dry-run chromium 2>/dev/null \
    | grep -oE 'Install location: +\S+' | awk '{print $NF}' | sort -u)"

  "$PW_NODE" "$PW_CLI" install chromium
  ok "Chromium ready at ${PLAYWRIGHT_BROWSERS_PATH} ($(printf '%s\n' "$PW_KEEP" | wc -l) components)"

  # Prune superseded revisions — a stale pair is ~920 MB of a build nothing can launch. Guarded on
  # every wanted component being present first: if the install above half-failed, the keep-list
  # would be the only thing standing between this and deleting the last working browser.
  pw_complete=1
  while IFS= read -r p; do
    [ -n "$p" ] && [ -d "$p" ] || pw_complete=0
  done <<< "$PW_KEEP"

  if [ "$pw_complete" -eq 1 ] && [ "$(printf '%s\n' "$PW_KEEP" | wc -l)" -ge 2 ]; then
    for d in "${PLAYWRIGHT_BROWSERS_PATH}"/*; do
      [ -d "$d" ] || continue
      # .links is Playwright's own bookkeeping; `chromium` is the convenience symlink repointed below.
      case "$(basename "$d")" in .links|chromium) continue ;; esac
      if ! printf '%s\n' "$PW_KEEP" | grep -qxF "$d"; then
        rm -rf "$d"
        ok "Pruned superseded $(basename "$d")"
      fi
    done
  else
    warn "Wanted browser components are not all present — skipping the prune rather than risk"
    warn "deleting the only usable build."
  fi

  # Repoint the convenience symlink the executablePath escape hatch uses. Nothing in the .NET test
  # tiers reads it — each resolves its own revision — so a stale one is invisible until something
  # follows that documented path and launches a browser whose protocol the driver does not speak.
  # The executable is searched for rather than assumed: r1194 unpacks to chrome-linux/, r1234 to
  # chrome-linux64/.
  PW_CHROMIUM_DIR="$(printf '%s\n' "$PW_KEEP" | grep -E '/chromium-[0-9]+$' | head -1)"
  if [ -n "$PW_CHROMIUM_DIR" ]; then
    PW_EXE="$(find "$PW_CHROMIUM_DIR" -maxdepth 2 -type f -name chrome 2>/dev/null | head -1)"
    if [ -n "$PW_EXE" ]; then
      ln -sfn "$PW_EXE" "${PLAYWRIGHT_BROWSERS_PATH}/chromium"
      ok "Repointed ${PLAYWRIGHT_BROWSERS_PATH}/chromium -> ${PW_EXE}"
    fi
  fi
fi

# The npm `playwright` package (the run-odyssey skill's driver) defaults to ~/.cache/ms-playwright,
# which is not where any of the above is. Redirect it for every future shell, so a Node consumer and
# the .NET tiers look in one place.
if [ -d /etc/profile.d ] && [ ! -f /etc/profile.d/playwright.sh ]; then
  printf 'export PLAYWRIGHT_BROWSERS_PATH="%s"\n' "$PLAYWRIGHT_BROWSERS_PATH" > /etc/profile.d/playwright.sh
  chmod 0644 /etc/profile.d/playwright.sh
  ok "Pinned PLAYWRIGHT_BROWSERS_PATH via /etc/profile.d"
fi

# ── 6. Verify ─────────────────────────────────────────────────────────────────────────────────────
# Each check proves a tier can RUN, not merely that a binary exists.
log "6/6  Verify"

dotnet --list-sdks | grep -q "^${DOTNET_MAJOR}\." && ok ".NET ${DOTNET_MAJOR} SDK: $(dotnet --version)" || warn ".NET ${DOTNET_MAJOR} SDK MISSING"
command -v dotnet-ef >/dev/null 2>&1 && ok "dotnet-ef present" || warn "dotnet-ef MISSING"
command -v mariadb   >/dev/null 2>&1 && ok "mariadb client present" || warn "mariadb client MISSING"

# Docker is preinstalled on this image; the DAEMON is what is down, and starting it here would not
# help — a build-time process does not survive into the session container. Odyssey.IntegrationTests
# SELF-SKIPS when the daemon is unreachable, so `dotnet test` would report success having never run
# that tier. Starting it is the SessionStart hook's job (.claude/hooks/session-start.sh), as is
# pre-pulling the images Testcontainers needs — both need a running daemon.
if command -v dockerd >/dev/null 2>&1; then
  ok "dockerd binary present (the session hook starts the daemon)"
else
  warn "dockerd NOT installed — Odyssey.IntegrationTests will silently self-skip"
fi

# Assert the revision, not the directory. "A browser exists" is exactly the state that costs a
# session a 650 MB download, because the one that exists can be the wrong one.
if [ -n "${PW_KEEP:-}" ] && [ "${pw_complete:-0}" -eq 1 ]; then
  ok "Playwright components baked in: $(printf '%s\n' "$PW_KEEP" | xargs -n1 basename | tr '\n' ' ')"
elif [ -d "${PLAYWRIGHT_BROWSERS_PATH}" ]; then
  warn "Browsers are present at ${PLAYWRIGHT_BROWSERS_PATH} but the pinned revision was NOT verified"
  warn "— the session hook will download it on first run"
else
  warn "No ${PLAYWRIGHT_BROWSERS_PATH} — Playwright will download Chromium on first run"
fi

if [ -n "$REPO_DIR" ]; then
  : > "$SETUP_LOG"

  # Release, matching .github/workflows/ci.yml's build step, plus -warnaserror on top of the
  # solution's own TreatWarningsAsErrors. If the SDK band were wrong, this is where it would show
  # rather than at restore. The output goes to a file because the alternative — "run it directly to
  # see why" — costs a full rebuild from inside a session that has no image-build console to read.
  if dotnet build "${REPO_DIR}/Odyssey.sln" -c Release --no-restore -warnaserror -v q --nologo >>"$SETUP_LOG" 2>&1; then
    ok "Solution builds clean at zero warnings (Release, as CI builds it)"

    # A clean compile does not prove the test host launches. This tier is 205 tests in well under a
    # second and needs neither Docker nor a running stack, so it is the cheapest possible proof that
    # VSTest, the xunit runner and the net10.0 test host actually work in this image.
    if dotnet test "${REPO_DIR}/Odyssey.ApiClient.Tests" -c Release --no-build --nologo >>"$SETUP_LOG" 2>&1; then
      ok "Test host runs (Odyssey.ApiClient.Tests green)"
    else
      warn "Odyssey.ApiClient.Tests FAILED — see ${SETUP_LOG}"
      tail -20 "$SETUP_LOG" || true
    fi
  else
    warn "Solution build FAILED — see ${SETUP_LOG}"
    tail -20 "$SETUP_LOG" || true
  fi
fi

log "Done."
```

Nothing here adds a third-party apt repository or executes a downloaded script: the .NET SDK and the
MariaDB client both come from Ubuntu's signed repository. That is the reason the client is 10.11
against an 11.4 server rather than version-matched — the wire protocol is stable across both, and an
unsigned third-party source would be a far worse trade than a client one minor behind. It is worth
having at all because its absence has no workaround from inside a session: the database is only ever
reachable as a container, so without a client the only way in is `docker exec`, which does not exist
while the stack is down and cannot reach a Testcontainers instance on an ephemeral port at all.

Two lines are deliberately weaker than they look. The `dockerd` line **checks** for the binary and
does not start it — see the table above. And the verify build writes to a log file instead of the
console, because the alternative — "run it directly to see why" — costs a full rebuild from inside a
session that has no image-build console left to read.

The verify step ends by running one test tier (`Odyssey.ApiClient.Tests`: 205 tests, under a second,
no Docker and no running stack). A clean compile does not prove the test host launches, and that is a
distinct failure with a distinct cause — the cheapest available proof that VSTest, the xunit runner
and the net10.0 test host all work in this image is to run them once.
