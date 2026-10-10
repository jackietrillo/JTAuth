# JTAuth — Build Prompt

## 0. Goal

Build **JTAuth**, the shared, passwordless identity service for all of Jackie Trillo's apps. Every app (CityBars first; later a bar-owner portal, MenuGenerator, …) signs its users in through JTAuth, so a person has **one account** across all of them. JTAuth knows **nothing** about any app's domain: no bars, cities, menus or app preferences. It answers only "who is this, and which app is this token for".

- The code name is **`JTAuth`** (root namespace, solution, repo, database, Azure resource prefix). `JT` is the umbrella for Jackie Trillo's apps.
- Apps depend on JTAuth; **JTAuth never depends on an app** (no app names in code, only in seed data for registered clients).
- First consumer: **CityBars** (`C:\JTRepos\CityBars`, spec `citybars-agent-prompt.md`, Section 4.4c), registered as client `citybars`.

**Reference:** CityBars uses the same stack, layering and conventions; when in doubt, match it. `JTAuth.BuildingBlocks` is a copy of `CityBars.BuildingBlocks` (handler abstractions, decorators, `Result<T>`, `AddHandlers`). When a third repository needs it, turn it into a NuGet package instead of copying again.

---

## 1. Stack

- **Backend:** C# / .NET 10, ASP.NET Core
- **Architecture:** Clean Architecture + CQRS (light) with directly injected handlers and **no mediator library** (Section 4)
- **Data access:** Dapper on MSSQL, in the `JTAuth` database. Local development uses a locally running SQL Server (Developer or Express); the same scripts and code run unchanged against Azure SQL. No EF Core, no ASP.NET Core Identity.
- **Tokens:** JWTs signed with an asymmetric key (RS256 or ES256) using `Microsoft.IdentityModel.JsonWebTokens`; Google ID tokens validated with Google's published keys.
- **Email:** Azure Communication Services Email in Azure; the log in Development.
- **Scripting:** PowerShell (DB deploy, run, test, provisioning)
- **Hosting (Azure), cost-first (Section 2c):** Azure SQL (serverless, auto-pause), App Service or Container Apps on the cheapest tier that works for the API, Key Vault for the signing key and the Google OAuth client secret, Application Insights with sampling and a daily cap, managed identity wherever possible. Bicep, driven by PowerShell, with a budget alert.

---

## 2. Naming and configuration

- Root namespace / solution / repo: **`JTAuth`**; projects `JTAuth.<Layer>` (`JTAuth.Application`, …).
- Database: **`JTAuth`**, every table in `dbo`. Connection string: `ConnectionStrings:JTAuthDb`.
- Azure resource prefix from a single parameter, e.g. `jta-<env>-<resource>`.
- Issuer: `JTAuth:Issuer` (the API's public base URL). Signing key: Key Vault in Azure; a development key file outside the repo locally (generated on first run, never committed).
- No app name appears in code. Registered apps are rows in `Client` (seed).
- Store timestamps in UTC (`datetime2(3)`).

### 2b. Local-first database

- Local default: `Server=localhost;Database=JTAuth;Trusted_Connection=True;TrustServerCertificate=True;`. With a named instance (for example `localhost\SQLEXPRESS`), set the user environment variable `ConnectionStrings__JTAuthDb` once; the API and the scripts read it.
- `scripts/deploy-db.ps1 -Environment local|azure` applies the same migrations either way; locally it creates the database if needed, on Azure the database comes from Bicep and sign-in uses Microsoft Entra ID.

---

## 2c. Cost rules

**Cost is a requirement.** Azure is the expensive part of these apps; the code is free, so pick the cheapest option that does the job, and add a paid service only with its free allowance, its trigger for paying and a cap. The rules (Azure sizing, email cap, caching) and the monthly cost estimate live in **[docs/cost-and-infrastructure.md](docs/cost-and-infrastructure.md)**, which is binding for every step that touches Azure. In short: develop for free (nothing needs Azure until Step 6), prefer free tiers and scale-to-zero, and re-read current pricing before building each item. The same rules apply to every app of Jackie Trillo's (see the CityBars cost doc).

---

## 3. Repository and folder structure

```
JTAuth/
├─ README.md
├─ JTAuth.sln
├─ jtauth-agent-prompt.md          # this spec
├─ global.json, Directory.Build.props, Directory.Packages.props, .editorconfig, nuget.config, .gitignore
├─ docs/
│  └─ architecture.md              # decisions (ADRs), token flow
├─ database/
│  ├─ migrations/                  # numbered, forward-only SQL scripts (DbUp)
│  ├─ seed/                        # the registered client apps
│  ├─ schema.sql                   # full reference schema
│  └─ JTAuth.Database/             # DbUp migrator (console), run by deploy-db.ps1
├─ infra/bicep/
├─ scripts/
│  ├─ deploy-db.ps1
│  ├─ run-local.ps1
│  └─ provision-azure.ps1
├─ JTAuth.BuildingBlocks/          # handler interfaces, decorators, Result, Registration/ (copied from CityBars)
├─ JTAuth.Contracts/               # API request/response DTOs only
├─ JTAuth.Domain/
├─ JTAuth.Application/
├─ JTAuth.Infrastructure/
├─ JTAuth.Api/                     # the only host
└─ tests/
   ├─ JTAuth.UnitTests/
   ├─ JTAuth.IntegrationTests/     # Dapper against real SQL Server (local or Testcontainers)
   └─ JTAuth.ArchitectureTests/
```

The projects sit at the repository root — there is no `src/` folder.

---

## 4. Architecture

Same rules as CityBars (its Sections 4.2–4.4b), in one service:

- **Domain** has no dependencies. **Application** depends on Domain, BuildingBlocks and Contracts, never on Dapper, ASP.NET, Azure SDKs or `System.Data`. **Infrastructure** implements Application's interfaces (repositories, token signer, email sender, Google token validator). **Api** is the composition root.
- One handler per command or query, injected by interface; cross-cutting decorators (logging, timing, exception mapping, validation) applied by `services.AddHandlers(assembly)`. No mediator, no `Send()`.
- Versioned routes `/api/v1/...`, `ProblemDetails` errors, `/health/live` and `/health/ready`, structured logging with correlation ids, OpenTelemetry, Swagger UI at `/swagger` in Development.

### 4.1 Sign-in (passwordless)

**There are no passwords anywhere** — no password column, no reset flow.

- **Phase one methods:** Google Sign-In, and a **one-time code sent by email**. **Phone (text-message code) is designed in but not built**: the `Phone` provider value and the code flow already allow for it, so adding it later means adding a message sender, not changing the model.
- **Signing up and signing in are the same flow.** The user enters an email, receives a 6-digit code, and enters it. If no account has that email, one is created and the response says `isNewUser`, so the app can ask for a display name (and its own extra fields, such as CityBars' city) before continuing.
- **Code rules:** 6 digits, valid for 10 minutes, single use, stored only as a hash, invalidated after 5 wrong attempts. Requesting a new code invalidates the previous one. Rate-limit requests per address and per IP (for example 1 per minute and 5 per hour). The request endpoint always answers the same way whether or not the address has an account. How the rules are met: the stored value is an HMAC-SHA256 of the address and the code keyed with a server secret (`JTAuth:CodeSecret`), because a million possible codes would fall to a plain hash if the table leaked. Each attempt is counted *before* the code is compared and only while the code is unused and under the limit, so a burst of parallel guesses still gets five. Using a code, and replacing it with a newer one, both end it; only the newest code for an address can be redeemed. Per-address limits (1 a minute, 5 an hour; `JTAuth:CodeRequestsPerAddressPerMinute` and `...PerHour`) are counted from the stored codes, so they hold across restarts and instances; per-IP limits (`RateLimits:CodeRequestsPerMinutePerIp` 5 and `CodeRequestsPerHourPerIp` 20, and `RequestsPerMinutePerIp` 120 for any endpoint) answer `429` with `Retry-After`; an app's web server passes each visitor's address in `X-Forwarded-For`, believed only from loopback, `RateLimits:TrustedProxies`, or any caller when `RateLimits:TrustAnyProxy` is set. Every way a code can be wrong (no code, wrong digits, expired, used, replaced, out of attempts) gets the same `401` answer.
- **Uniqueness:** each email address and each phone number belongs to at most one account — unique **individually**, never as a pair — enforced by the unique key on `UserIdentity (Provider, ProviderSubject)`. Store emails trimmed and lower-cased and phone numbers in E.164 format. A user can have one identity per provider and sign in with any of them.
- **Linking:** a Google sign-in whose verified email matches an existing Email identity signs in to that same account and adds the Google identity, instead of creating a second account.
- **Sending:** codes go through an `IEmailSender` interface in JTAuth.Application (with an `ISmsSender` beside it for later). In Development the implementation writes the code to the log. In Azure it uses Azure Communication Services Email. The email names the requesting app ("Your CityBars code is 123456") using the client's `Name`.

### 4.2 Clients and tokens

- **Apps are registered clients.** Each app is a row in `Client` (seeded). Every request that starts a sign-in carries the app's `clientId`; an unknown or disabled client is refused.
- **Access tokens:** short-lived (15 minutes) JWTs signed with an asymmetric key, with `iss` (JTAuth), `aud` (the client's `Audience`), `sub` (the user id, a Guid), `name` (display name, when set), `iat`, `exp` and `jti`. A token issued to one app is rejected by another because of `aud`. Tokens are signed RS256 with a 2048-bit RSA key whose `kid` is the SHA-256 of its public key; a client's token is addressed to the `Audience` of the client named in the request. Locally the key is a file generated on first run in `%LOCALAPPDATA%\JTAuth\signing-key.json` (override with `JTAuth:SigningKeyFile`); outside Development the path must be configured, until Key Vault replaces it.
- **Published keys:** `GET /.well-known/jwks.json` (public keys, with `kid`) and `GET /.well-known/openid-configuration` (issuer and JWKS URL), so apps validate tokens with the stock `AddJwtBearer` setup — no shared secret and no custom code. Key rotation: publish the new key before signing with it; keep the old one published until its tokens expire.
- **Refresh tokens:** opaque random values (256 bits), stored only as a SHA-256 hash, bound to one user and one client, 30-day lifetime, **rotated on every use** (the old one is revoked and the next token in the same chain is issued, valid 30 days from then, with no absolute cap). Each sign-in with a code starts its own **chain** (a family id), so a person signed in on a phone and a laptop has two chains. Presenting a revoked token again (reuse, or a token after logout) revokes the whole chain it belongs to, and only that chain. A token presented to a different app than it was issued to is refused without revoking anything. Every refresh failure is the same `401 invalid_refresh_token`. `/logout` revokes the presented token's chain (which holds one live token, the presented one) and answers `204` for any token, known or not. Two requests presenting the same token at once count as reuse: exactly one wins, the other ends the chain.
- **Permissions stay in the apps.** JTAuth never stores what a user may do inside an app. In phase one every client is **open**: any registered, enabled client accepts any verified user, so one account works in every app and each app decides what that person may do. That is deliberate (it is how "one account for all my apps" works), and it means a token for one app is something any signed-in person can obtain, which is why an app must never treat "has a token" as "may do this".
- **Which user has used which app is recorded anyway.** Every successful sign-in upserts a `UserClient` row (Section 5). Open clients ignore it when deciding who may sign in; it exists so an app can be given the contact details of its own users (Section 6) and so restricting an app later is only a rule, not a back-fill. Restricting who may sign in to an app (for example an invite-only internal tool) is a later, additive change: a migration adds `Client.AccessMode` (`Open` by default, or `Restricted`), and token issuing then also requires a `UserClient` row that is not revoked.
- **Later:** if full OpenID Connect is ever needed (third-party apps signing in through JTAuth, single sign-on across domains), move to OpenIddict or a hosted provider; the token shape stays the same, so apps barely change.

---

## 5. Data model (MSSQL, the `JTAuth` database)

Everything in `dbo`. Apps keep their own data in their own databases, keyed by the user id; there are no foreign keys to or from other databases.

- **Client:** Id (`int IDENTITY`, fixed in the seed), ClientId (`varchar(50)`, unique — e.g. `citybars`), Name (shown in sign-in emails), Audience (the `aud` claim), IsEnabled, ServiceKeyHash (nullable `binary(32)`, the SHA-256 of the key the app's servers use to call the contacts endpoint; the key itself is never stored), CreatedUtc. Seed `citybars`.
- **User:** Id (`uniqueidentifier`, `NEWSEQUENTIALID()` — the JWT `sub`; never guessable, never reveals user counts), DisplayName (nullable until the new-user step; the JWT `name`), CreatedUtc. No password and no email column. `USER` is reserved: always write `[User]`.
- **UserIdentity:** Id (`bigint`), UserId, Provider (Google / Email / Phone), ProviderSubject (Google subject id, normalized email, or E.164 phone), VerifiedUtc. Unique on (Provider, ProviderSubject) and on (UserId, Provider).
- **LoginCode:** Id (`bigint`), Provider (Email / Phone), ProviderSubject, CodeHash, ExpiresUtc, AttemptCount, ConsumedUtc, CreatedUtc. Index on (Provider, ProviderSubject).
- **RefreshToken:** Id (`bigint`), UserId, ClientId (FK `Client`), TokenHash (unique), ExpiresUtc, RevokedUtc, CreatedUtc, FamilyId (the chain a token belongs to) and ReplacedByTokenId (the token that replaced it, nullable; migration `0003_refresh_chain.sql`). Indexes on UserId and FamilyId.
- **UserClient:** UserId (FK `[User]`), ClientId (FK `Client`), FirstSignInUtc, LastSignInUtc, ContactConsentUtc (nullable: when the user agreed that this app may contact them; cleared to withdraw), RevokedUtc (nullable; used only when a client is `Restricted`). Primary key (UserId, ClientId). One row per person and app, created at the first successful sign-in to that app. Consent is **per app**: agreeing to CityBars' newsletter says nothing about any other app.

Enum-like columns are `varchar` with a `CHECK` constraint. Migrations in `database/migrations` are numbered and forward-only (never edit an applied one); `database/seed` is idempotent and runs on every deploy; `database/schema.sql` is the readable full reference.

---

## 6. API

All routes `/api/v1`, JSON, `ProblemDetails` on errors.

- `POST /auth/code/request` — body `{ clientId, email }`. Always `202 Accepted` with the same body, known address or not.
- `POST /auth/code/verify` — body `{ clientId, email, code }` → `{ accessToken, accessTokenExpiresUtc, refreshToken, refreshTokenExpiresUtc, isNewUser }`.- `POST /auth/google` — body `{ clientId, idToken }` → same response.
- `POST /auth/refresh` — body `{ clientId, refreshToken }` → the same response as verify with a new pair (rotation) and `isNewUser: false`. `401` for any token that is unknown, used, expired, logged out or another app's; `403` for a disabled client.
- `POST /auth/logout` — body `{ refreshToken }` → `204`, and the token (its chain) can no longer be refreshed. Access tokens already issued last out their 15 minutes.
- `GET /profile`, `PUT /profile`, with an access token for any registered, enabled app (checked against the published key; a disabled app's tokens stop working here). `GET` returns `{ displayName, signInMethods: [{ provider, status, address }] }`: Email is `Linked` (with its address) or `NotLinked`, Google is `Linked` or `NotLinked`, Phone is `ComingSoon`. `PUT` takes `{ displayName }` (trimmed, 1 to 50 characters, one line, no control characters) and returns the profile; the name is in the `name` claim of the access tokens issued from the next refresh on.
- **Changing the sign-in email** takes two calls, with a token: `POST /profile/email/request-code` `{ newEmail }` sends a code to the new address (naming the app of the token's `aud`) and answers `202` with the same body whether or not the address is free (no code is sent for an address another account owns, so it cannot be used to find out who has an account); asking for your own current address is `400`. `POST /profile/email/verify` `{ newEmail, code }` replaces the old address with the new one once the code is accepted (`400 invalid_code` otherwise, `409 email_in_use` if the address was taken in the meantime) and returns the profile. The code follows the sign-in code rules and limits. Afterwards the old address no longer signs in to this account (signing in with it starts a new account), and a person with no email (a Google-only account) gains one this way.
- `PUT /profile/contact-consent`, with a token — body `{ agreed }`. Records, or withdraws, the user's agreement that **the app the token is for** (`aud`) may contact them. An app asks for it at sign-up, with the plain words of what it will be used for (a newsletter, an owner's contact details shown to the app's support team).
- A **profile photo**: `PUT /profile/photo` (one image, multipart, JPEG, PNG or WebP, at most 2 MB, type judged from the bytes) and `DELETE /profile/photo`. It is opened and written again as a small WebP (256 px), the original is not kept, and the file is stored in blob storage under a name that never repeats, so it can be cached for a year. The profile and the access token carry its address as an optional `picture` claim, so an app can show it without calling JTAuth; with no photo the app shows the person's initials. A Google sign-in may supply the first picture. This is planned, not in a numbered step: it is built with CityBars' account menu.
- A **mobile number** is part of the profile (`GET`/`PUT /profile`), held as a contact number in E.164 form. It is not a sign-in method until phone sign-in is built, and then it is verified by a code like an email.
- `GET /contacts?after=&limit=` — **service-to-service**, not for browsers. The caller is the app's server, identified by its service key (`Authorization: Bearer <key>`, matched to `Client.ServiceKeyHash`); an app can only ever read its own client's contacts. Returns `{ items: [{ userId, displayName, email, mobile, consentUtc }], next }` for the users who have a `UserClient` row for that client, **have agreed** (`ContactConsentUtc` set) and are not revoked, in pages by `userId` (default 200, at most 1,000). `email` and `mobile` are null when the account has none. Never cached (`Cache-Control: no-store`), rate-limited, and every call is logged with the client and the number of rows returned. A missing or wrong key is `401`, a disabled client `403`. The `userId` is the token's `sub`, so the app can match each contact to its own records without JTAuth ever learning what the app does with them.
- `GET /.well-known/jwks.json`, `GET /.well-known/openid-configuration` (no `/api/v1` prefix; anonymous; cacheable).

---

## 7. Testing

- **Stack:** xUnit v3, NSubstitute, Shouldly; Microsoft Testing Platform. With SDK 10.0.401 `dotnet test` reports "Zero tests ran", so `scripts/run-tests.ps1` starts each test project's executable directly (unit and architecture tests; `-IncludeIntegration` adds the integration tests).
- **Unit tests:** code rules (expiry, single use, attempt limit, a new code invalidating the old one, rate limiting, identical response for known and unknown addresses); email normalization so one address cannot create two accounts; Google identity linking to an existing email account; token issuing and expiry; the audience matching the requesting client, and an unknown or disabled client refused; refresh rotation and reuse detection; the handler decorators (BuildingBlocks); database script checks (numbering, embedding, `schema.sql` complete, no password column).
- **Contacts tests** (the sign-in ones belong to Step 3, the rest to Step 7): a sign-in creates the `UserClient` row once and keeps `FirstSignInUtc` on later sign-ins; consent is per app (agreeing for one app lists the user for that app only); withdrawing consent removes the user from the list at once; the contacts endpoint refuses a missing, wrong or another app's key, never returns a user who has not agreed, never returns a user of another client, pages without gaps or repeats, and is not cacheable; the service key is stored only as a hash.
- **Integration tests:** the Dapper repositories against a real SQL Server in a throwaway database — the server named by `JTAUTH_TEST_SQLSERVER` (for example `localhost\SQLEXPRESS`) or a Testcontainers SQL Server; skipped, not failed, when neither is available.
- **Architecture tests:** Domain and Application never reference Infrastructure, Dapper, ASP.NET or Azure SDKs; Contracts are DTOs only; no mediator library; **no reference to any app** (JTAuth never depends on CityBars or any other app).
- **End-to-end check:** a token issued for `citybars` validates with stock `AddJwtBearer` configured with the JWKS URL and audience `citybars`, and fails with any other audience.

---

## 8. Build order

Work through these steps in order; each must be runnable and verifiable before the next. Steps 1, 2, 3 and 4 are complete. Start the next session at Step 5. Step 7 is planned and is built when CityBars needs it (its newsletter and owner contact details, CityBars Step 11).

1. **Solution skeleton. DONE.** The layout from Section 3, `JTAuth.BuildingBlocks` copied from CityBars, placeholder Contracts, the API host with health checks, and the three test projects.
   *Done when:* `dotnet build JTAuth.sln` is clean and the tests pass. Already true. Do not redo this step.

2. **Database and client seed. DONE.** The migrations, `schema.sql` and seed from Section 5 (the `Client`, `[User]`, `UserIdentity`, `LoginCode`, `RefreshToken` and `UserClient` tables, `Client.ServiceKeyHash`, and the `citybars` client), the DbUp migrator and `deploy-db.ps1`. `UserClient` and `ServiceKeyHash` came in migration `0002_user_client.sql`, so nothing needs adding to the database later for the contacts endpoint.
   *Done when:* `deploy-db.ps1 -Environment local` produces the `JTAuth` database with the `citybars` client, and a second run changes nothing. Already true. Do not redo this step.

3. **Email-code sign-in and tokens. DONE.** Code request and verify with every rule in Section 4.1 (codes written to the log in Development through `IEmailSender`; the Azure sender is not built), account creation, recording the person in `UserClient` at every successful sign-in (the first creates the row and sets `FirstSignInUtc`, later ones only move `LastSignInUtc`; `ContactConsentUtc` and `RevokedUtc` are never touched), RS256 access-token issuing, `/.well-known/jwks.json` and `openid-configuration`, client checks (an unknown client is `400`, a disabled one `403`, and `aud` is the requesting client's audience), Dapper repositories, and tests (unit, repository integration against a real SQL Server, and the end-to-end check in Section 7).
   *Done when:* you can request a code for `citybars`, read it from the log, verify it to create an account, sign in again the same way, and the returned JWT validates against the published keys with audience `citybars` (and fails with any other audience). Already true. Do not redo this step.

4. **Refresh, logout and profile. DONE.** Refresh-token rotation with reuse detection (a chain per sign-in, ended as a whole on reuse), logout, `GET/PUT /profile` (display name, sign-in methods) and the verified email change (Section 6). The sign-in response carries `refreshToken` and `refreshTokenExpiresUtc`; migration `0003_refresh_chain.sql` adds `FamilyId` and `ReplacedByTokenId`. JTAuth validates its own access tokens for the profile with the key it publishes.
   *Done when:* a refresh returns a new pair and revokes the old token, reusing a revoked token revokes the chain, and a display-name change shows up in the next access token's `name` claim.

5. **Google Sign-In.** Validate Google ID tokens, create or link accounts (Section 4.1 linking rule).
   *Done when:* a Google sign-in creates an account, and a Google sign-in with the verified email of an existing email account signs in to that same account.

6. **Azure and docs.** Azure Communication Services Email sender, Key Vault signing key, Bicep and `provision-azure.ps1`, `run-local.ps1`, `docs/architecture.md`, README.
   *Done when:* JTAuth runs in Azure, sends real code emails, and a fresh clone runs locally following only the README.

7. **Contacts for apps.** The tables already exist (migration 0002) and sign-in already writes `UserClient` (Step 3); this step adds the service key check (`Client.ServiceKeyHash`), a mobile number on the profile; `PUT /profile/contact-consent`; `GET /contacts` with the service key; tests from Section 7.
   *Done when:* after two people sign in to `citybars` and one of them agrees to be contacted, `GET /contacts` with CityBars' service key lists only that person, with their email and `sub`; withdrawing the consent removes them on the next call; and the same call with a wrong key, or another app's key, returns nothing.

---

## 9. Out of scope (phase one)

Passwords of any kind, phone (text-message) sign-in (designed in, not built), per-app access restrictions (`AccessMode`, designed in, not built: `UserClient` is recorded from the start, but no client is `Restricted`), any app's permissions or data, social providers other than Google, an admin UI, full OpenID Connect provider features (authorization code flow, consent screens, third-party clients).
