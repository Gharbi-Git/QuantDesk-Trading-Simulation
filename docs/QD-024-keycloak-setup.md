
# QD-024 — Keycloak Local Setup

## Purpose

Reproduce the QuantDesk Keycloak configuration on a fresh development environment.

## Prerequisites

- Docker Desktop running
- Local `infra/compose/.env` configured
- PostgreSQL and Keycloak Docker services

## Start the services

From the repository root:

    docker compose --env-file .\infra\compose\.env -f .\infra\compose\compose.yaml up -d postgres keycloak

## Realm import

Keycloak uses the realm configuration stored at:

    infra/keycloak/quantdesk-realm.json

The import creates:

- Realm: quantdesk
- Client: quantdesk-bff
- Client type: Confidential OIDC
- Authorization Code flow: Enabled
- Redirect URI: http://127.0.0.1:5080/signin-oidc
- Realm roles: PLAYER, ADMIN
- Token mapper: qd_roles

Keycloak imports this file on first initialization.

IMPORTANT: If the realm already exists, Keycloak skips the startup import. Changes to the JSON file will not automatically update an existing realm.

## Client secret

Do not commit the OIDC client secret to Git.

For a fresh installation:

1. Open http://127.0.0.1:8180/admin
2. Sign in with the locally configured Keycloak administrator account.
3. Select the quantdesk realm.
4. Open Clients > quantdesk-bff.
5. Open the Credentials tab.
6. Obtain the client secret.
7. Store the secret locally using ASP.NET Core User Secrets.

The API requires these configuration keys:

    Keycloak:Authority
    Keycloak:ClientId
    Keycloak:ClientSecret
    ConnectionStrings:QuantDesk

For the local host-run API:

    Keycloak:Authority = http://127.0.0.1:8180/realms/quantdesk
    Keycloak:ClientId = quantdesk-bff

Never commit actual passwords, connection strings, or client secrets.

## Test the configuration

Start the ASP.NET Core API in Development mode.

Open:

    http://127.0.0.1:5080/auth/login

Authenticate with a user who has the PLAYER role.

Verify:

    http://127.0.0.1:5080/api/me

The response should include the Keycloak subject and the internal QuantDesk user ID.

## Notes

- Keycloak credentials and QuantDesk application data are stored in separate PostgreSQL databases.
- The imported realm configuration does not include application users.
- Creating test users and assigning roles requires a separate setup step.
- The development HTTP settings must not be used unchanged in production.
