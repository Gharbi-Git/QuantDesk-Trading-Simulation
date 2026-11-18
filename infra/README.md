# Infrastructure — Developer 2 ownership

Docker Compose will provision PostgreSQL (separate application/Keycloak databases),
Keycloak, API, web, and engine services as needed. Use explicit image versions.
Avoid committing credentials or database volumes. A real Compose configuration will
be added in an implementation PR; no invalid placeholder Compose YAML is generated.