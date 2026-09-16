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
into the image goes stale the moment the package is bumped — the image that prompted this ships
`r1194` while the pinned 1.62.0 wants `r1234`. Installing the right build is only half of it: the
image also leaves `/opt/pw-browsers/chromium`, the convenience symlink the `executablePath` escape
hatch points at, aimed at **its** build, and that pin does not move on its own.

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
# Odyssey — Claude Code environment setup (Ubuntu 24.04). Runs as root at image build time.
set -euo pipefail

export DEBIAN_FRONTEND=noninteractive
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

REPO_DIR=""
for candidate in "${CLAUDE_PROJECT_DIR:-}" /home/user/odyssey "$PWD"; do
  [ -n "$candidate" ] && [ -f "${candidate}/Odyssey.sln" ] && { REPO_DIR="$candidate"; break; }
done

# 1. The .NET 10 SDK, from Ubuntu's own signed repository.
#    `apt-get update` is not optional even on a fresh image: the shipped index pins an exact
#    version, and Ubuntu supersedes the dotnet packages often enough that a stale index fails with a
#    bare `404 Not Found` on the .deb rather than anything naming the real problem.
if ! (command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks | grep -q '^10\.'); then
  apt-get update -qq
  apt-get install -y -qq --no-install-recommends dotnet-sdk-10.0
fi

# 2. dotnet-ef. Unpinned on purpose: the EF Core PACKAGES are 9.0.x while the tool resolves to 10.x,
#    because a 9.x tool cannot load the net10.0 startup assembly it has to reflect over.
export PATH="${HOME}/.dotnet/tools:${PATH}"
command -v dotnet-ef >/dev/null 2>&1 || dotnet tool install --global dotnet-ef
printf 'export PATH="$HOME/.dotnet/tools:$PATH"\n' > /etc/profile.d/dotnet-tools.sh

# 3. Warm the NuGet cache — the one step that turns a session's first build from minutes into
#    seconds, and pure filesystem state, so it survives into the image.
[ -n "$REPO_DIR" ] && dotnet restore "${REPO_DIR}/Odyssey.sln"

# 4. Verify, rather than assume. Each line proves a tier can run.
dotnet --list-sdks | grep -q '^10\.' || echo "WARNING: no .NET 10 SDK"
command -v dotnet-ef >/dev/null 2>&1 || echo "WARNING: dotnet-ef missing"
command -v dockerd    >/dev/null 2>&1 || echo "WARNING: dockerd missing — integration tests will self-skip"
[ -d /opt/pw-browsers ]               || echo "NOTE: no baked Playwright browsers; they download on first run"
[ -n "$REPO_DIR" ] && dotnet build "${REPO_DIR}/Odyssey.sln" -c Debug --no-restore -warnaserror -v q --nologo >/dev/null
```

Nothing here adds a third-party apt repository or executes a downloaded script; the SDK comes from
Ubuntu's signed repository. Note the `dockerd` line **checks** for the binary and does not start it —
see the table above.
