# JTAuth

The shared, passwordless sign-in service for all of Jackie Trillo's apps (CityBars first). One account per person across every app; each app is a registered client and validates JTAuth's tokens with standard JWT middleware. The spec and build plan live in [jtauth-agent-prompt.md](jtauth-agent-prompt.md).

## Status

Steps 1 (solution skeleton), 2 (database and client seed) and 3 (email-code sign-in and access tokens) are complete. The API signs people in with an emailed one-time code, creates the account on a first sign-in, and issues 15-minute RS256 access tokens that apps validate with stock `AddJwtBearer` against `/.well-known/jwks.json`. In Development the code is written to the log. Refresh tokens, profile and Google sign-in are the next steps.

## Build and test

Requires the .NET 10 SDK.

```powershell
dotnet build JTAuth.sln
./scripts/run-tests.ps1                                                       # unit and architecture tests
./scripts/run-tests.ps1 -IncludeIntegration -Server 'localhost\SQLEXPRESS'   # plus the SQL Server tests
```

`dotnet test` reports "Zero tests ran" on SDK 10.0.401, so the script starts each test project directly. The integration tests need SQL Server (the instance named by `-Server` or `JTAUTH_TEST_SQLSERVER`, or Docker) and are skipped without one.

## Database

One database, `JTAuth` (everything in `dbo`), read through `ConnectionStrings:JTAuthDb`. Requires a local SQL Server (Developer or Express) and Windows sign-in to it.

```powershell
./scripts/deploy-db.ps1                                  # default instance on localhost
./scripts/deploy-db.ps1 -Server 'localhost\SQLEXPRESS'   # named instance
```

The script creates the database if needed, applies the numbered migrations in `database/migrations` (each once) and re-runs the seed in `database/seed` (the registered client apps). Running it again is safe. `database/schema.sql` is the readable full schema.

With a named SQL Server instance, set the connection string once as a user environment variable:

```powershell
[Environment]::SetEnvironmentVariable('ConnectionStrings__JTAuthDb', 'Server=localhost\SQLEXPRESS;Database=JTAuth;Trusted_Connection=True;TrustServerCertificate=True;', 'User')
```

## Run the API locally

```powershell
./scripts/deploy-db.ps1 -Environment local -Server 'localhost\SQLEXPRESS'
$env:ConnectionStrings__JTAuthDb = 'Server=localhost\SQLEXPRESS;Database=JTAuth;Trusted_Connection=True;TrustServerCertificate=True;'
dotnet run --project JTAuth.Api          # https://localhost:7200, Swagger at /swagger
```

```powershell
curl.exe -X POST https://localhost:7200/api/v1/auth/code/request -H "Content-Type: application/json" -d '{"clientId":"citybars","email":"you@example.com"}'
# the code is in the API's console log: "Your CityBars code is 123456"
curl.exe -X POST https://localhost:7200/api/v1/auth/code/verify -H "Content-Type: application/json" -d '{"clientId":"citybars","email":"you@example.com","code":"123456"}'
```

Settings (`JTAuth` section): `Issuer` (the API's public URL), `CodeSecret` (keys the stored code hashes), `SigningKeyFile` (defaults in Development to `%LOCALAPPDATA%\JTAuth\signing-key.json`, generated on first run and never committed), and the per-address code limits. `RateLimits` holds the per-IP limits. Outside Development the API refuses to start until a real email sender is registered.

## Layout

| Folder | Contents |
|---|---|
| `JTAuth.BuildingBlocks` | Handler abstractions, decorators, `Result<T>`, `AddHandlers` (copied from CityBars) |
| `JTAuth.Contracts` | API DTOs only |
| `JTAuth.{Domain,Application,Infrastructure,Api}` | The service, Clean Architecture |
| `database/` | `migrations/`, `seed/`, `schema.sql`, and `JTAuth.Database` (the DbUp migrator) |
| `scripts/` | PowerShell: `deploy-db.ps1`, `run-tests.ps1` |
| `tests/` | `JTAuth.UnitTests`, `JTAuth.IntegrationTests`, `JTAuth.ArchitectureTests` |

## Registering an app

Add a row to `database/seed/0001_clients.sql` (a new, never-reused `Id`, the `ClientId` the app sends, the `Name` shown in sign-in emails, and the token `Audience`) and redeploy. The app then validates tokens with `AddJwtBearer` pointed at JTAuth's URL, with its own audience.
