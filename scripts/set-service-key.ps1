<#
.SYNOPSIS
    Sets the service key an app's server uses to call JTAuth on its own behalf (for example CityBars asking for its members' emails).

.DESCRIPTION
    Only the SHA-256 of the key is stored (Client.ServiceKeyHash); the key itself is never kept here, so it cannot be read back.
    Give the same key to the app (CityBars: JTAuth__ServiceKey, Key Vault in Azure). Without -Key a new random key is made and
    printed once. Setting a key again replaces the old one, which stops working at once. -Remove clears it, so the app cannot ask.

.EXAMPLE
    ./scripts/set-service-key.ps1 -ClientId citybars -Server 'localhost\SQLEXPRESS' -Key 'development-jtauth-service-key-for-citybars'
.EXAMPLE
    ./scripts/set-service-key.ps1 -ClientId citybars -Server 'localhost\SQLEXPRESS'    # makes and prints a new key
.EXAMPLE
    ./scripts/set-service-key.ps1 -ClientId citybars -Server jta-dev-sql.database.windows.net -Environment azure
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ClientId,

    [string] $Key,

    [switch] $Remove,

    [ValidateSet('local', 'azure')]
    [string] $Environment = 'local',

    [string] $Server = 'localhost',

    [string] $Database = 'JTAuth'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($ClientId -notmatch '^[a-z0-9-]{1,50}$') { throw 'ClientId must be lowercase letters, digits and hyphens.' }

if ($Remove) {
    $hash = 'NULL'
}
else {
    $generated = $false
    if (-not $Key) {
        $bytes = [byte[]]::new(32)
        [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
        $Key = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        $generated = $true
    }
    if ($Key.Trim().Length -lt 24) { throw 'The key must be at least 24 characters.' }

    $digest = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($Key.Trim()))
    $hash = '0x' + [Convert]::ToHexString($digest)
}

$authArgs = @(if ($Environment -eq 'azure') { '-G' } else { '-E' })
$query = "SET NOCOUNT ON; UPDATE dbo.Client SET ServiceKeyHash = $hash WHERE ClientId = '$ClientId'; SELECT @@ROWCOUNT;"
$rows = & sqlcmd -S $Server -d $Database @authArgs -b -Q $query
if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed (exit code $LASTEXITCODE)." }
$affected = @($rows | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ -match '^\d+$' }) | Select-Object -Last 1
if ($affected -ne '1') { throw "There is no client '$ClientId' in $Server/$Database." }

if ($Remove) {
    Write-Host "Service key for '$ClientId' removed." -ForegroundColor Green
}
else {
    Write-Host "Service key for '$ClientId' set." -ForegroundColor Green
    if ($generated) {
        Write-Host "The key (shown once, not stored anywhere): $Key" -ForegroundColor Yellow
    }
}