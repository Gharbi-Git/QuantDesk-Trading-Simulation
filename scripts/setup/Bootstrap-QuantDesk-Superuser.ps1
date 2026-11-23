# Once-per-environment explicit SUPERUSER promotion of an EXISTING QuantDesk registrant.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Username, [string]$KeycloakUrl='http://127.0.0.1:8180', [string]$RealmName='quantdesk')
$ErrorActionPreference='Stop'
$KeycloakUrl=$KeycloakUrl.TrimEnd('/')
if ($KeycloakUrl -notmatch '^http://(localhost|127\.0\.0\.1)(:\d+)?$|^https://') { throw 'Use localhost or HTTPS.' }
$envPath=Join-Path (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path 'infra/compose/.env'
function ReadEnv([string]$key) {
 if (-not (Test-Path $envPath)) { return $null }
 $line=Get-Content $envPath | Where-Object { $_ -match "^\s*$([regex]::Escape($key))=" } | Select-Object -Last 1
 if (-not $line) { return $null }
 return (($line -split '=',2)[1].Trim().Trim('"').Trim("'"))
}
$admin=ReadEnv 'QD_KC_ADMIN_USERNAME';if (-not $admin) {$admin='admin'}
$pwd=ReadEnv 'QD_KC_ADMIN_PASSWORD'
if (-not $pwd) {
 $sec=Read-Host 'Keycloak master admin password' -AsSecureString
 $ptr=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
 try {$pwd=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)}
 finally {[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)}
}
$auth=Invoke-RestMethod -Uri "$KeycloakUrl/realms/master/protocol/openid-connect/token" -Method Post -ContentType 'application/x-www-form-urlencoded' -Body @{grant_type='password';client_id='admin-cli';username=$admin;password=$pwd}
$pwd=$null
$headers=@{Authorization="Bearer $($auth.access_token)"}
$root="$KeycloakUrl/admin/realms/$RealmName"
$encoded=[Uri]::EscapeDataString($Username)
$response = Invoke-RestMethod -Uri "$root/users?username=$encoded&exact=true&max=10" -Headers $headers
$found = @($response | ForEach-Object { $_ })
$matches=@($found | Where-Object { $_.username -ceq $Username -and $_.enabled -eq $true -and $_.username -notlike 'service-account-*' })
if ($matches.Count -ne 1) { throw "Found $($matches.Count) eligible accounts with that exact username; stop." }
$response = Invoke-RestMethod -Uri "$root/users/$($matches[0].id)/role-mappings/realm/composite" -Headers $headers
$existing = @($response | ForEach-Object { $_ })
if (@($existing | Where-Object { $_.name -eq 'SUPERUSER' }).Count -gt 0) { Write-Host 'That user is already SUPERUSER.';exit 0 }
$confirmation=Read-Host "Type PROMOTE $Username to confirm SUPERUSER assignment"
if ($confirmation -cne "PROMOTE $Username") {throw 'Cancelled: no privileges changed.'}
$role=Invoke-RestMethod -Uri "$root/roles/SUPERUSER" -Headers $headers
$body=ConvertTo-Json -InputObject @($role) -Depth 8 -Compress
Invoke-RestMethod -Uri "$root/users/$($matches[0].id)/role-mappings/realm" -Method Post -Headers $headers -ContentType 'application/json' -Body $body | Out-Null
Write-Host "Keycloak SUPERUSER granted to $Username. Sign out and sign in again to get the new token."
Write-Host 'No registration form can grant SUPERUSER or ADMIN.'
