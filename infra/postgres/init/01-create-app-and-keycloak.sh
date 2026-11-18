#!/usr/bin/env bash
set -euo pipefail
: "${QD_APP_DB_PASSWORD:?Missing QD_APP_DB_PASSWORD}"
: "${QD_KC_DB_PASSWORD:?Missing QD_KC_DB_PASSWORD}"

# Runs once on a brand-new PostgreSQL Docker volume; never modifies an existing DB.
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  -v app_pw="$QD_APP_DB_PASSWORD" -v kc_pw="$QD_KC_DB_PASSWORD" <<'SQL'
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', 'qd_app', :'app_pw') \gexec
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', 'qd_keycloak', :'kc_pw') \gexec
SELECT format('CREATE DATABASE %I OWNER %I', 'quantdesk', 'qd_app') \gexec
SELECT format('CREATE DATABASE %I OWNER %I', 'keycloak', 'qd_keycloak') \gexec
SQL