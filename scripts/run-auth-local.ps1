<#
.SYNOPSIS
    Runs JTAuth, the sign-in service, locally at https://localhost:7200. Ctrl+C stops it.

.DESCRIPTION
    Applies any new database migrations (the same ones every environment gets), builds the API, and runs it in this window in the
    Development environment. Leave the window open: JTAuth sends no email in Development, it writes each sign-in code to this log as
        Email to someone@example.com: Your CityBars code is 123456
    so this is where to read the code after pressing "Email me a code".

    Connection string, first match wins:
      1. -ConnectionString
      2. -Server (with -Database)
      3. the ConnectionStrings__JTAuthDb environment variable
      4. Server=localhost

    The address is fixed at https://localhost:7200 (and http://localhost:5200): it is the issuer that apps check tokens against.
    Needs the ASP.NET Core development certificate to be trusted (dotnet dev-certs https --trust), as the apps call it over HTTPS.

.EXAMPLE
    ./scripts/run-auth-local.ps1 -Server 'localhost\SQLEXPRESS'
.EXAMPLE
    ./scripts/run-auth-local.ps1 -Server 'localhost\SQLEXPRESS' -SkipDeploy   # do not touch the database schema
#>
[CmdletBinding()]
param(
    [string] $Server,

    [string] $Database = 'JTAuth',

    [string] $ConnectionString,

    # Skip applying migrations and the seed before starting.
    [switch] $SkipDeploy
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$apiProject = Join-Path $repoRoot 'JTAuth.Api/JTAuth.Api.csproj'
$httpsPort = 7200
$httpPort = 5200

if (-not $ConnectionString) {
    if ($Server) { $ConnectionString = "Server=$Server;Database=$Database;Trusted_Connection=True;TrustServerCertificate=True;" }
    elseif ($env:ConnectionStrings__JTAuthDb) { $ConnectionString = $env:ConnectionStrings__JTAuthDb }
    else { $ConnectionString = "Server=localhost;Database=$Database;Trusted_Connection=True;TrustServerCertificate=True;" }
}

if (-not $SkipDeploy) {
    & (Join-Path $PSScriptRoot 'deploy-db.ps1') -Environment local -ConnectionString $ConnectionString
}

# A copy of JTAuth still running from an earlier start would hold the ports and lock the files the build replaces, so stop it first.
$mine = $PID
$stale = Get-CimInstance Win32_Process | Where-Object {
    $_.ProcessId -ne $mine -and $_.CommandLine -match 'JTAuth\.Api' -and $_.CommandLine -notmatch 'run-auth-local'
}
foreach ($process in $stale) {
    Write-Host "Stopping an earlier JTAuth ($($process.Name), process $($process.ProcessId))..." -ForegroundColor Yellow
    Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
}
if ($stale) { Start-Sleep -Seconds 2 }

foreach ($port in $httpsPort, $httpPort) {
    $listener = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($listener) {
        $owner = (Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue).ProcessName
        throw "Port $port is already in use by '$owner' (process $($listener.OwningProcess)). Stop it and run this again."
    }
}

Write-Host 'Building JTAuth...' -ForegroundColor Cyan
& dotnet build $apiProject --nologo --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'Building JTAuth failed.' }

# The connection string is passed to this process only; nothing is changed machine-wide.
$env:ConnectionStrings__JTAuthDb = $ConnectionString
$env:ASPNETCORE_ENVIRONMENT = 'Development'

# A copy of everything shown here is kept in logs/jtauth.log (git-ignored, replaced at each start), so a sign-in code can also be
# read from the file when the window is hidden or scrolled: Get-Content logs/jtauth.log -Tail 20
$logFile = Join-Path $repoRoot 'logs/jtauth.log'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $logFile) | Out-Null

Write-Host ''
Write-Host 'JTAuth is starting:' -ForegroundColor Green
Write-Host "  https://localhost:$httpsPort   (health: /health/ready, keys: /.well-known/jwks.json, Swagger: /swagger)"
Write-Host '  Sign-in codes appear in this window as: Email to <address>: Your CityBars code is <code>'
Write-Host "  The same log is kept in $logFile"
Write-Host '  Press Ctrl+C to stop.'
Write-Host ''

& dotnet run --project $apiProject --no-build --no-launch-profile --urls "https://localhost:$httpsPort;http://localhost:$httpPort" 2>&1 | Tee-Object -FilePath $logFile
