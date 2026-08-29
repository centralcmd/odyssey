# Getting help

Odyssey is a personal project maintained in spare time. There is no commercial support and no
SLA — but questions are welcome, and this page says where to put them so they get an answer.

## Where to go

| You have… | Go to |
|---|---|
| A **question** — how to configure something, whether a behaviour is intended, how a feature is meant to work | [**Discussions**](https://github.com/centralcmd/odyssey/discussions) |
| A **bug** — something demonstrably broken, with steps to reproduce | [Bug report](https://github.com/centralcmd/odyssey/issues/new?template=bug_report.yml) |
| A **feature idea** | [Feature request](https://github.com/centralcmd/odyssey/issues/new?template=feature_request.yml) |
| A **security vulnerability** | **Not an issue and not a discussion** — see [`SECURITY.md`](SECURITY.md) |
| A **Code of Conduct concern** | <odyssey-code-of-conduct.trustee054@passmail.net> |

Blank issues are disabled deliberately. If your report does not fit either form, it is almost
certainly a question — start a discussion and it can be turned into an issue from there.

## Read these first

Most self-hosting questions are already answered:

- [**README**](README.md) — running the stack (Docker Compose, Aspire), the `.env`
  file, demo data and the seeded logins.
- [**`docs/deployment.md`**](docs/deployment.md) — the supported production deployment: the
  `.env.prod` values, TLS, the bootstrap administrator, backups, the Data Protection key ring,
  upload size limits and the security checklist.
- [**`docs/running-locally-alongside-a-live-stack.md`**](docs/running-locally-alongside-a-live-stack.md)
  — if Compose and Aspire are fighting over ports 3307 / 5188 / 5199.
- [**CONTRIBUTING**](CONTRIBUTING.md) — building, the test tiers, migrations, code style.

Two things that come up often enough to answer here:

- **SMTP is required in Production.** An empty `Email:SmtpHost` fails startup on purpose: the
  no-relay fallback logs the action link instead of sending it, which for a password reset means
  writing a working credential into the log. See *First-run notes* in `docs/deployment.md`.
- **Most policy is not in `appsettings.json`.** Upload caps, retention, throttles and the
  file-analysis switch are admin-editable at `/settings` and stored in the database, so they
  change without a redeploy. If you are looking for a configuration key and cannot find one,
  look there first.

## What helps a question get answered

- Which way you are running it — Compose, Aspire, `dotnet run`, or the released images — and the
  version from `GET /api/healthz`.
- What you expected and what happened instead.
- The relevant log lines from the container that failed (`docker compose logs api`), with
  anything secret removed.

## What to expect

You should get a reply within a few days. Please do not `@`-mention the maintainer to bump a
thread. Answers are best-effort, and "not planned" is a real possible outcome — this is one
person's project, run in the open, rather than a product with a roadmap owed to anyone.
