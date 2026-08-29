-- Runs once, on first start of an empty data directory, in BOTH stacks — the bind mount in
-- docker-compose.yml is inherited by the production overlay. Keep it to schema bootstrapping only.
--
-- The application user is created by the mariadb image itself from MARIADB_USER/MARIADB_PASSWORD and
-- is already granted on MARIADB_DATABASE, so no GRANT belongs here. An orphan
-- `GRANT ALL PRIVILEGES ON odyssey.* TO 'admin'@'%'` lived here for a user this repository never
-- creates; it was removed in issue #451 §1.5.
CREATE DATABASE IF NOT EXISTS odyssey;
FLUSH PRIVILEGES;
