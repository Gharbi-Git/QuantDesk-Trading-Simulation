[CmdletBinding()]
param(
    [string]$KeycloakUrl = 'http://127.0.0.1:8180',
    [string]$RealmName = 'quantdesk',
    [string]$OidcClientId = 'quantdesk-bff'
)

$ErrorActionPreference = 'Stop'
$KeycloakUrl = $KeycloakUrl.TrimEnd('/')

if (
    $KeycloakUrl -notmatch '^https://[^/]+$' -and
    $KeycloakUrl -notmatch '^http://(localhost|127\.0\.0\.1)(:\d+)?$'
) {
    throw 'Keycloak requires HTTPS or local HTTP.'
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$envPath = Join-Path $repoRoot 'infra/compose/.env'

if (-not (Test-Path -LiteralPath $envPath)) {
    throw "Environment file not found: $envPath"
}

function Read-LocalEnvValue {
    param([string]$Key)

    $line = Get-Content -LiteralPath $envPath |
        Where-Object {
            $_ -match "^\s*$([regex]::Escape($Key))="
        } |
        Select-Object -Last 1

    if ($null -eq $line) {
        return $null
    }

    $value = ($line -split '=', 2)[1].Trim()

    if (
        $value.Length -ge 2 -and
        (
            ($value.StartsWith('"') -and $value.EndsWith('"')) -or
            ($value.StartsWith("'") -and $value.EndsWith("'"))
        )
    ) {
        return $value.Substring(1, $value.Length - 2)
    }

    return $value
}

function Expand-KeycloakItems {
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) {
        return
    }

    if ($Value -is [System.Array]) {
        foreach ($item in $Value) {
            Expand-KeycloakItems -Value $item
        }
    }
    else {
        Write-Output $Value
    }
}

function Get-KeycloakItems {
    param([Parameter(Mandatory)][string]$Uri)

    $response = Invoke-RestMethod `
        -Method Get `
        -Uri $Uri `
        -Headers $headers

    Expand-KeycloakItems -Value $response
}

$adminUser = Read-LocalEnvValue 'QD_KC_ADMIN_USERNAME'
$adminPassword = Read-LocalEnvValue 'QD_KC_ADMIN_PASSWORD'

if ([string]::IsNullOrWhiteSpace($adminUser)) {
    $adminUser = 'admin'
}

if ([string]::IsNullOrWhiteSpace($adminPassword)) {
    $secure = Read-Host 'Keycloak master admin password' -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)

    try {
        $adminPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
    }
}

try {
    $tokenResponse = Invoke-RestMethod `
        -Method Post `
        -Uri "$KeycloakUrl/realms/master/protocol/openid-connect/token" `
        -ContentType 'application/x-www-form-urlencoded' `
        -Body @{
            grant_type = 'password'
            client_id = 'admin-cli'
            username = $adminUser
            password = $adminPassword
        }
}
finally {
    $adminPassword = $null
}

if ([string]::IsNullOrWhiteSpace([string]$tokenResponse.access_token)) {
    throw 'Keycloak authentication failed.'
}

$headers = @{
    Authorization = "Bearer $($tokenResponse.access_token)"
}

$tokenResponse = $null

$encodedRealm = [uri]::EscapeDataString($RealmName)
$api = "$KeycloakUrl/admin/realms/$encodedRealm"

$realmInfo = Invoke-RestMethod `
    -Method Get `
    -Uri $api `
    -Headers $headers

if (-not $realmInfo.registrationAllowed) {
    $body = @{
        realm = $RealmName
        registrationAllowed = $true
    } | ConvertTo-Json -Depth 5

    Invoke-RestMethod `
        -Method Put `
        -Uri $api `
        -Headers $headers `
        -ContentType 'application/json' `
        -Body $body | Out-Null

    Write-Host 'Self-registration enabled.'
}
else {
    Write-Host 'Self-registration already enabled.'
}

$playerRole = Invoke-RestMethod `
    -Method Get `
    -Uri "$api/roles/PLAYER" `
    -Headers $headers

if (-not $playerRole.id) {
    throw 'PLAYER role is missing or invalid.'
}

$superuserRoles = @(
    Get-KeycloakItems -Uri "$api/roles?search=SUPERUSER" |
        Where-Object {
            $_.name -eq 'SUPERUSER'
        }
)

if ($superuserRoles.Count -eq 0) {
    $body = @{
        name = 'SUPERUSER'
        description = 'QuantDesk application owner'
    } | ConvertTo-Json -Depth 5

    Invoke-RestMethod `
        -Method Post `
        -Uri "$api/roles" `
        -Headers $headers `
        -ContentType 'application/json' `
        -Body $body | Out-Null

    Write-Host 'SUPERUSER role created.'
}

$defaultRoleName = "default-roles-$RealmName"
$encodedDefaultRole = [uri]::EscapeDataString($defaultRoleName)

$defaultRole = Invoke-RestMethod `
    -Method Get `
    -Uri "$api/roles/$encodedDefaultRole" `
    -Headers $headers

if (-not $defaultRole.id) {
    throw "Default role not found: $defaultRoleName"
}

$compositeUri = "$api/roles/$encodedDefaultRole/composites"

$composites = @(
    Get-KeycloakItems -Uri "$compositeUri/realm"
)

$privilegedDefaults = @(
    $composites | Where-Object {
        $_.name -in @('ADMIN', 'SUPERUSER')
    }
)

if ($privilegedDefaults.Count -gt 0) {
    $body = ConvertTo-Json `
        -InputObject $privilegedDefaults `
        -Depth 10 `
        -Compress

    Invoke-RestMethod `
        -Method Delete `
        -Uri $compositeUri `
        -Headers $headers `
        -ContentType 'application/json' `
        -Body $body | Out-Null

    Write-Host 'Privileged default roles removed.'
}

$composites = @(
    Get-KeycloakItems -Uri "$compositeUri/realm"
)

$hasPlayer = @(
    $composites | Where-Object {
        $_.name -eq 'PLAYER'
    }
).Count -gt 0

if (-not $hasPlayer) {
    $body = ConvertTo-Json `
        -InputObject @($playerRole) `
        -Depth 10 `
        -Compress

    Invoke-RestMethod `
        -Method Post `
        -Uri $compositeUri `
        -Headers $headers `
        -ContentType 'application/json' `
        -Body $body | Out-Null

    Write-Host 'Automatic PLAYER role configured.'
}
else {
    Write-Host 'PLAYER is already a default role.'
}

$first = 0
$repaired = 0
$skipped = 0

while ($true) {
    $users = @(
        Get-KeycloakItems -Uri "$api/users?first=$first&max=100"
    )

    if ($users.Count -eq 0) {
        break
    }

    foreach ($u in $users) {
        if (
            [string]::IsNullOrWhiteSpace([string]$u.id) -or
            $u.username -like 'service-account-*' -or
            $u.serviceAccountClientId
        ) {
            $skipped++
            continue
        }

        $effectiveRoles = @(
            Get-KeycloakItems `
                -Uri "$api/users/$($u.id)/role-mappings/realm/composite"
        )

        $hasApplicationRole = @(
            $effectiveRoles | Where-Object {
                $_.name -in @('PLAYER', 'ADMIN', 'SUPERUSER')
            }
        ).Count -gt 0

        if ($hasApplicationRole) {
            $skipped++
            continue
        }

        $body = ConvertTo-Json `
            -InputObject @($playerRole) `
            -Depth 10 `
            -Compress

        Invoke-RestMethod `
            -Method Post `
            -Uri "$api/users/$($u.id)/role-mappings/realm" `
            -Headers $headers `
            -ContentType 'application/json' `
            -Body $body | Out-Null

        $repaired++
    }

    $first += $users.Count

    if ($users.Count -lt 100) {
        break
    }
}

Write-Host "Existing users repaired: $repaired; skipped: $skipped"

if ([string]::IsNullOrWhiteSpace($OidcClientId)) {
    throw 'OidcClientId must be specified.'
}

$encodedClientId = [uri]::EscapeDataString($OidcClientId)

$clients = @(
    Get-KeycloakItems -Uri "$api/clients?clientId=$encodedClientId" |
        Where-Object {
            $_.clientId -eq $OidcClientId
        }
)

Write-Host "Matching OIDC clients: $($clients.Count)"

if ($clients.Count -ne 1) {
    throw "Expected one OIDC client '$OidcClientId', found $($clients.Count)."
}

$uuid = [string]$clients[0].id

if ([string]::IsNullOrWhiteSpace($uuid)) {
    throw 'OIDC client UUID is missing.'
}

$clientDetails = Invoke-RestMethod `
    -Method Get `
    -Uri "$api/clients/$uuid" `
    -Headers $headers

if ($clientDetails.clientId -ne $OidcClientId) {
    throw 'OIDC client identity mismatch.'
}

if ($clientDetails.protocol -ne 'openid-connect') {
    throw 'The selected client is not an OpenID Connect client.'
}

if ($clientDetails.publicClient -ne $false) {
    throw "OIDC client '$OidcClientId' must have Client authentication enabled."
}

if ($clientDetails.enabled -ne $true) {
    throw "OIDC client '$OidcClientId' is disabled."
}

$mapperUri = "$api/clients/$uuid/protocol-mappers/models"

$mappers = @(
    Get-KeycloakItems -Uri $mapperUri
)

$matchingMappers = @(
    $mappers | Where-Object {
        $null -ne $_.config -and
        $_.config.'claim.name' -eq 'qd_roles'
    }
)

if ($matchingMappers.Count -gt 1) {
    throw 'Multiple qd_roles protocol mappers were found.'
}

$expectedConfig = @{
    'claim.name' = 'qd_roles'
    'jsonType.label' = 'String'
    'multivalued' = 'true'
    'id.token.claim' = 'true'
    'access.token.claim' = 'true'
    'userinfo.token.claim' = 'false'
}

if ($matchingMappers.Count -eq 0) {
    $model = @{
        name = 'QuantDesk Realm Roles'
        protocol = 'openid-connect'
        protocolMapper = 'oidc-usermodel-realm-role-mapper'
        config = $expectedConfig
    }

    $body = $model | ConvertTo-Json -Depth 10

    Invoke-RestMethod `
        -Method Post `
        -Uri $mapperUri `
        -Headers $headers `
        -ContentType 'application/json' `
        -Body $body | Out-Null

    Write-Host "qd_roles mapper created for $OidcClientId."
}
else {
    $existing = $matchingMappers[0]

    if ($existing.protocolMapper -ne 'oidc-usermodel-realm-role-mapper') {
        throw 'Existing qd_roles mapper uses an unexpected provider.'
    }

    if ([string]::IsNullOrWhiteSpace([string]$existing.id)) {
        throw 'Existing qd_roles mapper ID is missing.'
    }

    $config = @{}

    if ($null -ne $existing.config) {
        foreach ($property in $existing.config.PSObject.Properties) {
            $config[$property.Name] = [string]$property.Value
        }
    }

    $changed = $false

    foreach ($key in $expectedConfig.Keys) {
        if ($config[$key] -ne $expectedConfig[$key]) {
            $config[$key] = $expectedConfig[$key]
            $changed = $true
        }
    }

    if ($changed) {
        $existing.config = $config

        $body = $existing | ConvertTo-Json -Depth 10

        Invoke-RestMethod `
            -Method Put `
            -Uri "$mapperUri/$($existing.id)" `
            -Headers $headers `
            -ContentType 'application/json' `
            -Body $body | Out-Null

        Write-Host 'qd_roles mapper updated.'
    }
    else {
        Write-Host 'qd_roles mapper already configured.'
    }
}

$finalRoles = @(
    Get-KeycloakItems -Uri "$compositeUri/realm"
)

$playerDefault = @(
    $finalRoles | Where-Object {
        $_.name -eq 'PLAYER'
    }
)

if ($playerDefault.Count -ne 1) {
    throw 'PLAYER default-role verification failed.'
}

$privilegedDefaults = @(
    $finalRoles | Where-Object {
        $_.name -in @('ADMIN', 'SUPERUSER')
    }
)

if ($privilegedDefaults.Count -gt 0) {
    throw 'SECURITY: ADMIN and SUPERUSER must not be default roles.'
}

$verifiedMappers = @(
    Get-KeycloakItems -Uri $mapperUri
)

$verifiedMapper = @(
    $verifiedMappers | Where-Object {
        $_.config.'claim.name' -eq 'qd_roles' -and
        $_.protocolMapper -eq 'oidc-usermodel-realm-role-mapper' -and
        $_.config.'id.token.claim' -eq 'true' -and
        $_.config.'multivalued' -eq 'true'
    }
)

if ($verifiedMapper.Count -ne 1) {
    throw 'qd_roles mapper verification failed.'
}

Write-Host 'SUCCESS: QuantDesk self-registration configured.'
Write-Host 'PLAYER is the default application role.'
Write-Host 'ADMIN and SUPERUSER are not default roles.'
Write-Host "OIDC role mapper verified for $OidcClientId."
Write-Host 'Sign out and sign back in to refresh authorization claims.'
