# Trusting the HTTPS development certificate on Linux

Running the Aspire AppHost on Linux shows a banner in the dashboard:

> No trusted development certificate was found. See https://aka.ms/aspire/devcerts for more
> information.

This document explains what the banner actually means, why it appears even though
[the README](../README.md#aspire-orchestration) says Aspire runs the app over HTTP, and gives
the complete command sequence to clear it.

None of this applies to the Docker Compose stack, which is HTTP end to end.

## Why the banner appears when "Aspire doesn't need HTTPS"

Both statements are true, because they are about different processes:

| Process | Scheme | Needs the dev cert? |
|---|---|---|
| API (`5188`) and client (`5199`) resources | HTTP | No |
| The **Aspire dashboard** itself | HTTPS (`https://localhost:17047`) | **Yes** |

The AppHost's default `https` launch profile
(`Odyssey.AppHost/Properties/launchSettings.json`) serves the dashboard and its OTLP, MCP and
resource-service endpoints over HTTPS. So the app resources genuinely do not need a
certificate — the dashboard hosting them does.

That is why the banner is a real diagnostic and not noise, and also why it is survivable: with
the banner showing, the stack still runs and the app is still reachable on `http://localhost:5199`.

## The three legs of trust, and why the banner is all-or-nothing

Aspire raises the banner from `dotnet dev-certs https --check --trust`, which on Linux reports
success only when the certificate is trusted in **all** of three independent places. It is
normal to have one or two of them already working and still see the banner.

| Leg | Where trust lives | Who consults it |
|---|---|---|
| .NET | `~/.dotnet/corefx/cryptography/x509stores/my` | the runtime itself |
| OpenSSL | a directory listed in `SSL_CERT_DIR` | `curl`, most CLI tooling |
| NSS | `~/.pki/nssdb`, written with `certutil` | the Chromium family, Firefox |

Two of these have a trap that makes them fail quietly:

- **OpenSSL trust does nothing until `SSL_CERT_DIR` is set.** `dotnet dev-certs https --trust`
  writes the certificate and its hash symlink into `~/.aspnet/dev-certs/trust`, then tells you —
  in a line easily lost in its output — that the directory has no effect until it is named in
  `SSL_CERT_DIR`. Until you export it, that leg reports `The certificate is not trusted by
  OpenSSL`.
- **The NSS leg needs a tool the distribution does not install by default.** Without `certutil`,
  `dotnet dev-certs https --trust` exits non-zero with *"The certificate is only partially
  trusted"* — and because the check is all-or-nothing, the banner stays up even once the other
  two legs are green.

## Commands

Run these in order. The first needs `sudo`; the rest are per-user.

### 1. Install `certutil`

```bash
sudo dnf install -y nss-tools          # Fedora / RHEL
# sudo apt install -y libnss3-tools    # Debian / Ubuntu
# sudo pacman -S --needed nss          # Arch
```

### 2. Put `SSL_CERT_DIR` on your shell sessions

```bash
mkdir -p ~/.bashrc.d
cat > ~/.bashrc.d/aspnet-dev-certs.sh <<'EOF'
export SSL_CERT_DIR="$HOME/.aspnet/dev-certs/trust:/etc/pki/tls/certs"
EOF
```

> **Keep the system CA directory in that list.** `SSL_CERT_DIR` *replaces* OpenSSL's CApath
> rather than adding to it, so naming only the dev-cert directory would break validation of
> every public CA for anything that honours the variable. The second entry is
> `/etc/pki/tls/certs` on Fedora/RHEL and `/etc/ssl/certs` on Debian/Ubuntu and Arch — confirm
> yours with `openssl version -d` and check it holds the hashed symlinks:
>
> ```bash
> ls /etc/pki/tls/certs | grep -cE '^[0-9a-f]{8}\.[0-9]+$'   # expect a few hundred, not 0
> ```

### 3. Put it on your desktop session too, for IDE-launched runs

Skip this if you only ever start the AppHost from a terminal.

Rider, Visual Studio Code and anything else launched from the desktop never read `.bashrc`, so
they will not see step 2. On a systemd user session:

```bash
mkdir -p ~/.config/environment.d
cat > ~/.config/environment.d/50-aspnet-dev-certs.conf <<'EOF'
SSL_CERT_DIR=${HOME}/.aspnet/dev-certs/trust:/etc/pki/tls/certs
EOF
```

This applies at your **next login**. Until then, launching the IDE from a terminal that has
already sourced step 2 gets the same result.

### 4. Trust the certificate

```bash
dotnet dev-certs https --trust
```

This is also the step that *creates* the certificate if you do not have one yet. Re-running it
is safe and is what writes into the NSS database now that step 1 has provided `certutil`.

### 5. Verify

```bash
dotnet dev-certs https --check --trust   # expect exit code 0
```

Add `--verbose` if it still fails; it names the specific leg. Confirm you have not broken
ordinary TLS at the same time:

```bash
curl -sS -o /dev/null -w '%{http_code}\n' https://api.nuget.org/v3/index.json   # expect 200
```

### 6. Restart

Restart the AppHost, and your browser if you want it to stop warning about the dashboard
certificate. If you did step 3, log out and back in.

## Escape hatch: run the AppHost without HTTPS

If you would rather not touch your machine's trust stores, run the AppHost's `http` launch
profile instead. The dashboard is then served over HTTP, no certificate is involved, and the
banner does not appear:

```bash
dotnet run --project Odyssey.AppHost --launch-profile http
```

The app resources are unaffected either way — they were already HTTP.

## macOS and Windows

`dotnet dev-certs https --trust` is the whole procedure. It writes to the system keychain or
the Windows certificate store, both of which the browsers already consult, so there is no
`SSL_CERT_DIR` step and no NSS step.
