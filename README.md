# JTAuth

The shared, passwordless sign-in service for all of Jackie Trillo's apps (CityBars first). One account per person across every app; each app is a registered client and validates JTAuth's tokens with standard JWT middleware. The spec and build plan live in [jtauth-agent-prompt.md](jtauth-agent-prompt.md).

## Status

Steps 1 (solution skeleton) and 2 (database and client seed) are complete. The API is an empty host with `/health/live` and `/health/ready`.

## Build and test

Requires the .NET 10 SDK.

```powershell
dotnet build JTAuth.sln
dotnet test --solution JTAuth.sln
```

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

## Layout

| Folder | Contents |
|---|---|
| `JTAuth.BuildingBlocks` | Handler abstractions, decorators, `Result<T>`, `AddHandlers` (copied from CityBars) |
| `JTAuth.Contracts` | API DTOs only |
| `JTAuth.{Domain,Application,Infrastructure,Api}` | The service, Clean Architecture |
| `database/` | `migrations/`, `seed/`, `schema.sql`, and `JTAuth.Database` (the DbUp migrator) |
| `scripts/` | PowerShell: `deploy-db.ps1` |
| `tests/` | `JTAuth.UnitTests`, `JTAuth.IntegrationTests`, `JTAuth.ArchitectureTests` |

## Registering an app

Add a row to `database/seed/0001_clients.sql` (a new, never-reused `Id`, the `ClientId` the app sends, the `Name` shown in sign-in emails, and the token `Audience`) and redeploy. The app then validates tokens with `AddJwtBearer` pointed at JTAuth's URL, with its own audience.
