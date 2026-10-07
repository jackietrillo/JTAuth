<#
.SYNOPSIS
    Runs the JTAuth tests: unit and architecture by default, integration with -IncludeIntegration.

.DESCRIPTION
    Starts each test project directly (dotnet run) and fails if any of them fails.
    `dotnet test --solution` is not used: with the .NET 10.0.401 SDK it reports "Zero tests ran" for every project
    here, although the tests themselves pass.

    The integration tests need SQL Server: pass -Server (for example 'localhost\SQLEXPRESS'), or set
    JTAUTH_TEST_SQLSERVER, or have Docker running for a Testcontainers server. With none of them they are skipped.
    They deploy the real migrations and seed into a throwaway database and drop it afterwards.

.EXAMPLE
    ./scripts/run-tests.ps1
.EXAMPLE
    ./scripts/run-tests.ps1 -IncludeIntegration -Server 'localhost\SQLEXPRESS'
#>
[CmdletBinding()]
param(
    [switch] $IncludeIntegration,

    [string] $Server,

    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$projects = @('JTAuth.UnitTests', 'JTAuth.ArchitectureTests')
if ($IncludeIntegration) { $projects += 'JTAuth.IntegrationTests' }
if ($Server) { $env:JTAUTH_TEST_SQLSERVER = $Server }

if (-not $NoBuild) {
    & dotnet build (Join-Path $repoRoot 'JTAuth.sln') --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }
}

$failed = @()
foreach ($project in $projects) {
    Write-Host "`n== $project" -ForegroundColor Cyan
    & dotnet run --project (Join-Path $repoRoot "tests/$project") --no-build
    if ($LASTEXITCODE -ne 0) { $failed += "$project (exit code $LASTEXITCODE)" }
}

if ($failed.Count -gt 0) {
    Write-Host "`nFAILED: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}

Write-Host "`nAll test projects passed." -ForegroundColor Green
