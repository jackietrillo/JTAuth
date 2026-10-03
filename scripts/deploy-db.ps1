<#
.SYNOPSIS
    Creates or upgrades the JTAuth database: applies the numbered migrations, then the seed.

.DESCRIPTION
    The same migrations and seed run for local SQL Server and Azure SQL; only the connection differs.
    Migrations are journaled in dbo.SchemaVersions and run once each. Seed scripts (the registered client
    apps) are idempotent and run on every deploy.

    Connection string, first match wins:
      1. -ConnectionString
      2. -Server (with -Database)
      3. the ConnectionStrings__JTAuthDb environment variable (what the API reads)
      4. local only: Server=localhost

.EXAMPLE
    ./scripts/deploy-db.ps1                                   # default instance on localhost
.EXAMPLE
    ./scripts/deploy-db.ps1 -Server 'localhost\SQLEXPRESS'    # named instance
.EXAMPLE
    ./scripts/deploy-db.ps1 -Environment azure -Server jta-dev-sql.database.windows.net
    # Signs in with Microsoft Entra ID (Active Directory Default: az login, managed identity, ...).
#>
[CmdletBinding()]
param(
    [ValidateSet('local', 'azure')]
    [string] $Environment = 'local',

    [string] $Server,

    [string] $Database = 'JTAuth',

    [string] $ConnectionString,

    [switch] $SkipSeed
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$migratorProject = Join-Path $repoRoot 'database/JTAuth.Database/JTAuth.Database.csproj'

function New-ConnectionString([string] $environment, [string] $server, [string] $database) {
    if ($environment -eq 'azure') {
        return "Server=tcp:$server,1433;Database=$database;Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;"
    }
    return "Server=$server;Database=$database;Trusted_Connection=True;TrustServerCertificate=True;"
}

function Get-ConnectionPart([string] $connectionString, [string[]] $keys) {
    foreach ($part in $connectionString -split ';') {
        $name, $value = $part -split '=', 2
        if ($value -and $keys -contains $name.Trim()) { return $value.Trim() }
    }
    return '?'
}

if (-not $ConnectionString) {
    if ($Server) {
        $ConnectionString = New-ConnectionString $Environment $Server $Database
    }
    elseif ($env:ConnectionStrings__JTAuthDb) {
        $ConnectionString = $env:ConnectionStrings__JTAuthDb
    }
    elseif ($Environment -eq 'local') {
        $ConnectionString = New-ConnectionString $Environment 'localhost' $Database
    }
    else {
        throw 'Azure needs -Server (e.g. jta-dev-sql.database.windows.net) or -ConnectionString.'
    }
}

# Show where we are deploying without echoing credentials.
$where = '{0} / {1}' -f (Get-ConnectionPart $ConnectionString 'Server', 'Data Source'),
                        (Get-ConnectionPart $ConnectionString 'Database', 'Initial Catalog')
Write-Host "Deploying JTAuth database ($Environment): $where" -ForegroundColor Cyan

$migratorArgs = @()
if ($Environment -eq 'local') { $migratorArgs += '--ensure-database' }  # Azure SQL databases are created by Bicep.
if ($SkipSeed) { $migratorArgs += '--skip-seed' }

& dotnet build $migratorProject --configuration Release --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw "Building the database migrator failed (exit code $LASTEXITCODE)." }

# Pass the connection string through the environment, not the command line, so it never shows in process lists.
$previous = $env:ConnectionStrings__JTAuthDb
$env:ConnectionStrings__JTAuthDb = $ConnectionString
try {
    & dotnet run --project $migratorProject --configuration Release --no-build -- @migratorArgs
    if ($LASTEXITCODE -ne 0) { throw "JTAuth database deploy failed (exit code $LASTEXITCODE)." }
}
finally {
    $env:ConnectionStrings__JTAuthDb = $previous
}

Write-Host "JTAuth database is ready: $where" -ForegroundColor Green
