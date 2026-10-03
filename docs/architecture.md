# JTAuth Architecture

Living document; the spec is [jtauth-agent-prompt.md](../jtauth-agent-prompt.md). Completed in Step 6.

## Where JTAuth sits

```
 CityBars Web ──(email + code, clientId=citybars)──►  JTAuth API ──► JTAuth database
      │                                                  │            (Client, [User], UserIdentity,
      │◄──────── access token (aud=citybars) ────────────┘             LoginCode, RefreshToken)
      │
      └──(Bearer token)──► CityBars Bars Service ──(fetches /.well-known/jwks.json once, caches)──► JTAuth API
                                 │
                                 └─ uses sub as UserId, copies name into its own Member table
```

- Apps depend on JTAuth; JTAuth never depends on an app (architecture test).
- Apps never read the JTAuth database; they learn about a user only from the token.
- What a user may do inside an app lives in that app's database.

## Decisions (ADRs)

| # | Decision | Why |
|---|---|---|
| 1 | Own small identity service, not ASP.NET Core Identity | Identity is EF Core-based and password-centric, and needs OpenIddict on top to issue multi-app tokens. JTAuth issues standard JWTs (asymmetric key, JWKS endpoint) that any app validates with stock `AddJwtBearer`; it can move to OpenIddict or a hosted provider later without changing the token shape. |
| 2 | Separate repository and database, named `JTAuth` | One account across all of Jackie Trillo's apps; the umbrella prefix keeps app names out of shared infrastructure. |
| 3 | `JTAuth.BuildingBlocks` copied from CityBars | About 300 lines; a NuGet package is worth it only when a third repository needs it. |
| 4 | Registered clients in a `Client` table with per-client audience | Each app's tokens are rejected by every other app; sign-in emails name the right app. |
