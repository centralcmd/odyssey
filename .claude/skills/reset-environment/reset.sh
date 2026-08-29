#!/usr/bin/env bash
#
# Reset (wipe + reseed) the Odyssey dev/test database.
#
# Drops and recreates the database, then runs Odyssey.MigrationService, which re-applies all
# migrations and re-runs the (idempotent, deterministic) demo seeder against the now-empty DB.
# A plain restart does NOT reseed — the seeder skips when its sentinel is already present — which
# is why this script wipes first.
#
# Works against BOTH local stacks, which both publish MariaDB on host port 3307:
#   * Docker Compose  — credentials come from the repo .env (MARIADB_USER/PASSWORD)
#   * Aspire AppHost  — credentials come from AppHost user-secrets (Aspire:MariaDb:*)
# The script gathers candidate credentials from both sources and uses the first that
# authenticates, so it "just works" whichever stack is running. It talks to MariaDB through a
# throwaway `mariadb` client container on the host network (no local client needed) and connects
# as the application user, whose db-scoped ALL privilege covers dropping/recreating its own
# database (and survives the drop) — no root password required.
#
# Override anything with env vars: DB_HOST, DB_PORT, DB_NAME, DB_USER, DB_PASSWORD, MARIADB_IMAGE.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

DB_HOST="${DB_HOST:-127.0.0.1}"
DB_PORT="${DB_PORT:-3307}"
MARIADB_IMAGE="${MARIADB_IMAGE:-mariadb:11.4}"
EXPLICIT_USER="${DB_USER:-}"
EXPLICIT_PASSWORD="${DB_PASSWORD:-}"

# --- gather credentials from .env (Compose) and user-secrets (Aspire) -----------------------
# Read specific keys from .env without executing it (values may contain shell metacharacters).
env_val() {
  [ -f "$REPO_ROOT/.env" ] || return 0
  grep -E "^$1=" "$REPO_ROOT/.env" | tail -1 | cut -d= -f2- | tr -d '\r' | sed -e 's/^"//' -e 's/"$//'
}
ENV_USER="$(env_val MARIADB_USER)"
ENV_PASSWORD="$(env_val MARIADB_PASSWORD)"
ENV_DB="$(env_val ODYSSEY_DATABASE)"; [ -n "$ENV_DB" ] || ENV_DB="$(env_val MARIADB_DATABASE)"

ASPIRE_USER=""; ASPIRE_PASSWORD=""
if command -v dotnet >/dev/null 2>&1; then
  SECRETS="$(dotnet user-secrets list --project "$REPO_ROOT/Odyssey.AppHost" 2>/dev/null || true)"
  ASPIRE_USER="$(printf '%s\n' "$SECRETS" | sed -n 's/^Aspire:MariaDb:User = //p' | head -1)"
  ASPIRE_PASSWORD="$(printf '%s\n' "$SECRETS" | sed -n 's/^Aspire:MariaDb:Password = //p' | head -1)"
fi

DB_NAME="${DB_NAME:-${ENV_DB:-odyssey}}"

mariadb_client() { # usage: mariadb_client <user> <password> <extra args...>
  local user="$1" password="$2"; shift 2
  docker run --rm --network host -e MYSQL_PWD="$password" "$MARIADB_IMAGE" \
    mariadb -h "$DB_HOST" -P "$DB_PORT" -u"$user" "$@"
}

# --- resolve which credentials actually authenticate ----------------------------------------
DBUSER=""; DBPASS=""; CRED_SRC=""
try_cred() { # <user> <password> <source-label>
  [ -n "$1" ] || return 1
  if mariadb_client "$1" "$2" -N -e "SELECT 1;" >/dev/null 2>&1; then
    DBUSER="$1"; DBPASS="$2"; CRED_SRC="$3"; return 0
  fi
  return 1
}

echo "==> Locating a running MariaDB on $DB_HOST:$DB_PORT and resolving credentials"
try_cred "$EXPLICIT_USER" "$EXPLICIT_PASSWORD" "explicit DB_USER/DB_PASSWORD" \
  || try_cred "$ENV_USER" "$ENV_PASSWORD" ".env (Compose)" \
  || try_cred "$ASPIRE_USER" "$ASPIRE_PASSWORD" "Aspire user-secrets" \
  || try_cred "odyssey" "odyssey_password" "Compose defaults" \
  || {
    echo "ERROR: could not authenticate to MariaDB at $DB_HOST:$DB_PORT." >&2
    echo "       Is the Compose or Aspire stack running? Override with DB_USER/DB_PASSWORD." >&2
    exit 1
  }
echo "    using credentials from: $CRED_SRC (user '$DBUSER', database '$DB_NAME')"

echo "==> Wiping database '$DB_NAME'"
mariadb_client "$DBUSER" "$DBPASS" -e "DROP DATABASE IF EXISTS \`$DB_NAME\`; CREATE DATABASE \`$DB_NAME\`;"

echo "==> Re-applying migrations and reseeding demo data"
CONN="server=$DB_HOST;port=$DB_PORT;database=$DB_NAME;user=$DBUSER;password=$DBPASS;"
ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}" \
Seed__DemoData="${SEED_DEMO_DATA:-true}" \
ConnectionStrings__OdysseyConnection="$CONN" \
  dotnet run --project "$REPO_ROOT/Odyssey.MigrationService" -c Release

echo "==> Verifying seeded data"
mariadb_client "$DBUSER" "$DBPASS" -N -e "
SELECT CONCAT('  accounts       = ', COUNT(*)) FROM \`$DB_NAME\`.Accounts;
SELECT CONCAT('  users          = ', COUNT(*)) FROM \`$DB_NAME\`.AspNetUsers;
SELECT CONCAT('  exchange_rates = ', COUNT(*)) FROM \`$DB_NAME\`.ExchangeRates;
SELECT CONCAT('  transactions   = ', COUNT(*)) FROM \`$DB_NAME\`.Transactions;"

echo "==> Reset complete. (Running API/client need no restart — data is read live from the DB.)"
