
# QuantDesk - Keycloak Management Client Setup
# One-time LOCAL setup. Never commit Keycloak secrets to Git.

[CmdletBinding()]
param(
    [string]$KeycloakUrl = 'http://127.0.0.1:8180',
    [string]$RealmName = 'quantdesk'
)

$ErrorActionPreference = 'Stop'

# ------------------------------------------------------------
# 1. Validate configuration
# ------------------------------------------------------------

$KeycloakUrl = $KeycloakUrl.TrimEnd('/')

if ($KeycloakUrl -notmatch '^http://(localhost|127\.0\.0\.1)(:\d+)?$|^https://') {
    throw 'Use HTTPS or localhost for administrative credentials.'
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$envPath = Join-Path $repoRoot 'infra/compose/.env'
$apiProject = Join-Path $repoRoot 'apps/api/src/QuantDesk.Api/QuantDesk.Api.csproj'

if (-not (Test-Path $apiProject)) {
    throw "API project not found: $apiProject"
}

function ReadEnv([string]$key) {
    if (-not (Test-Path $envPath)) {
        return $null
    }

    $line = Get-Content $envPath |
        Where-Object {
            $_ -match "^\s*$([regex]::Escape($key))="
        } |
        Select-Object -Last 1

    if (-not $line) {
        return $null
    }

    return (($line -split '=', 2)[1].Trim().Trim('"').Trim("'"))
}

Write-Host 'Configuring QuantDesk Keycloak management client...'

# ------------------------------------------------------------
# 2. Authenticate with Keycloak master realm
# ------------------------------------------------------------

$user = ReadEnv 'QD_KC_ADMIN_USERNAME'

if (-not $user) {
    $user = 'admin'
}

$password = ReadEnv 'QD_KC_ADMIN_PASSWORD'

if (-not $password) {
    $sec = Read-Host 'Keycloak master admin password' -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)

    try {
        $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
    }
}

try {
    $token = Invoke-RestMethod `
        -Method Post `
        -Uri "$KeycloakUrl/realms/master/protocol/openid-connect/token" `
        -ContentType 'application/x-www-form-urlencoded' `
        -Body @{
            grant_type = 'password'
            client_id  = 'admin-cli'
            username   = $user
            password   = $password
        }
}
finally {
    $password = $null
}

if (-not $token.access_token) {
    throw 'Keycloak authentication failed.'
}

$headers = @{
    Authorization = "Bearer $($token.access_token)"
}

$token = $null

$encodedRealm = [uri]::EscapeDataString($RealmName)
$base = "$KeycloakUrl/admin/realms/$encodedRealm"

Write-Host 'Keycloak authentication successful.'

# ------------------------------------------------------------
# 3. Safe Keycloak client lookup
# ------------------------------------------------------------

function Get-KcClient {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $encodedName = [uri]::EscapeDataString($Name)

    $response = Invoke-RestMethod `
        -Method Get `
        -Uri "$base/clients?clientId=$encodedName" `
        -Headers $headers

    # Explicitly enumerate responses.
    # Avoid treating an empty REST response as a valid client.
    foreach ($item in $response) {
        if (
            $null -ne $item -and
            $item.clientId -eq $Name -and
            -not [string]::IsNullOrWhiteSpace([string]$item.id)
        ) {
            Write-Output $item
        }
    }
}

# ------------------------------------------------------------
# 4. Find or create management client
# ------------------------------------------------------------

$clientId = 'quantdesk-admin-management'

$clients = @(Get-KcClient -Name $clientId)

if ($clients.Count -eq 0) {

    Write-Host 'Creating Keycloak management client...'

    $body = @{
        clientId                  = $clientId
        enabled                   = $true
        protocol                  = 'openid-connect'
        publicClient              = $false
        serviceAccountsEnabled    = $true
        standardFlowEnabled       = $false
        directAccessGrantsEnabled = $false
        fullScopeAllowed          = $true
    } | ConvertTo-Json -Depth 10

    Invoke-RestMethod `
        -Method Post `
        -Uri "$base/clients" `
        -Headers $headers `
        -ContentType 'application/json' `
        -Body $body | Out-Null

    $clients = @(Get-KcClient -Name $clientId)
}

if ($clients.Count -ne 1) {
    throw "Expected exactly one Keycloak management client, found $($clients.Count)."
}

$cid = [string]$clients[0].id

if ([string]::IsNullOrWhiteSpace($cid)) {
    throw 'Keycloak management client UUID is missing.'
}

Write-Host 'Management client found.'

# ------------------------------------------------------------
# 5. Ensure the management client has a service account
# ------------------------------------------------------------

$clientDetails = Invoke-RestMethod `
    -Method Get `
    -Uri "$base/clients/$cid" `
    -Headers $headers

$needsUpdate = (
    -not $clientDetails.enabled -or
    $clientDetails.publicClient -or
    -not $clientDetails.serviceAccountsEnabled
)

if ($needsUpdate) {

    Write-Host 'Enabling management client service account...'

    $clientDetails.enabled = $true
    $clientDetails.publicClient = $false
    $clientDetails.serviceAccountsEnabled = $true

    $updateBody = $clientDetails | ConvertTo-Json -Depth 30

    Invoke-RestMethod `
        -Method Put `
        -Uri "$base/clients/$cid" `
        -Headers $headers `
        -ContentType 'application/json' `
        -Body $updateBody | Out-Null
}

$account = Invoke-RestMethod `
    -Method Get `
    -Uri "$base/clients/$cid/service-account-user" `
    -Headers $headers

if (-not $account.id) {
    throw 'Keycloak service-account user was not returned.'
}

Write-Host 'Service account verified.'

# ------------------------------------------------------------
# 6. Find realm-management client
# ------------------------------------------------------------

$rm = @(Get-KcClient -Name 'realm-management')

if ($rm.Count -ne 1) {
    throw 'Keycloak realm-management client is missing or ambiguous.'
}

$rmid = [string]$rm[0].id

if ([string]::IsNullOrWhiteSpace($rmid)) {
    throw 'realm-management client UUID is missing.'
}

# ------------------------------------------------------------
# 7. Assign required management permissions
# ------------------------------------------------------------

$required = @(
    'view-users',
    'manage-users',
    'view-realm'
)

$roles = @()

foreach ($name in $required) {

    $role = Invoke-RestMethod `
        -Method Get `
        -Uri "$base/clients/$rmid/roles/$name" `
        -Headers $headers

    if (-not $role.id) {
        throw "Required Keycloak role is missing: $name"
    }

    $roles += $role
}

$roleBody = ConvertTo-Json `
    -InputObject @($roles) `
    -Depth 10 `
    -Compress

Invoke-RestMethod `
    -Method Post `
    -Uri "$base/users/$($account.id)/role-mappings/clients/$rmid" `
    -Headers $headers `
    -ContentType 'application/json' `
    -Body $roleBody | Out-Null

Write-Host 'Management permissions configured.'

# ------------------------------------------------------------
# 8. Retrieve client secret
# ------------------------------------------------------------

$secretReply = Invoke-RestMethod `
    -Method Get `
    -Uri "$base/clients/$cid/client-secret" `
    -Headers $headers

if ([string]::IsNullOrWhiteSpace([string]$secretReply.value)) {
    throw 'No Keycloak management client secret returned.'
}

# ------------------------------------------------------------
# 9. Save configuration in .NET user-secrets
# ------------------------------------------------------------

# User-secrets are stored outside the Git repository.

dotnet user-secrets init --project $apiProject | Out-Null

if ($LASTEXITCODE -ne 0) {
    throw 'dotnet user-secrets init failed.'
}

function Set-ApiSecret {
    param(
        [string]$Key,
        [string]$Value
    )

    dotnet user-secrets set $Key $Value --project $apiProject | Out-Null

    if ($LASTEXITCODE -ne 0) {
        throw "Failed saving configuration key: $Key"
    }
}

Set-ApiSecret `
    -Key 'Keycloak:ManagementBaseUrl' `
    -Value $KeycloakUrl

Set-ApiSecret `
    -Key 'Keycloak:ManagementRealm' `
    -Value $RealmName

Set-ApiSecret `
    -Key 'Keycloak:ManagementClientId' `
    -Value $clientId

Set-ApiSecret `
    -Key 'Keycloak:ManagementClientSecret' `
    -Value $secretReply.value

$secretReply = $null

# ------------------------------------------------------------
# 10. Completion
# ------------------------------------------------------------

Write-Host ''
Write-Host '----------------------------------------'
Write-Host 'QuantDesk Keycloak management configured.'
Write-Host '----------------------------------------'
Write-Host ''
Write-Host 'Management client credentials saved in local .NET user-secrets.'
Write-Host 'No client secret was written to the Git repository.'
Write-Host 'Restart the API in Development mode to load the settings.'
Write-Host ''
Write-Host 'SECURITY: manage-users is a privileged permission.'
Write-Host 'Restrict service-account permissions and client scopes before production.'
