# JTAuth Architecture

Living document; the spec is [jtauth-agent-prompt.md](../jtauth-agent-prompt.md). Completed in Step 6.

## Where JTAuth sits

```
 CityBars Web ──(email + code, clientId=citybars)──►  JTAuth API ──► JTAuth database
      │                                                  │            (Client, [User], UserIdentity,
      │◄──────── access token (aud=citybars) ────────────┘             LoginCode, RefreshToken, UserClient)
      │
      └──(Bearer token)──► CityBars Bars Service ──(fetches /.well-known/jwks.json once, caches)──► JTAuth API
                                 │
                                 └─ uses sub as UserId, copies name into its own Member table
```

- Apps depend on JTAuth; JTAuth never depends on an app (architecture test).
- Apps never read the JTAuth database; they learn about a user only from the token.
- What a user may do inside an app lives in that app's database.

## Sign-in with an emailed code

```
App ─ POST /api/v1/auth/code/request {clientId, email} ─► RequestLoginCodeHandler ─► LoginCode (hash only) ─► IEmailSender
App ─ POST /api/v1/auth/code/verify  {clientId, email, code} ─► VerifyLoginCodeHandler
        client ok? ► latest code usable? ► attempt claimed? ► hash matches? ► code consumed?
        ► person found or created ► UserClient upsert ► access token (aud = the client's Audience)
```

- **One handler per request** (`JTAuth.Application/SignIn`, `Keys`), injected by interface into thin controllers (`JTAuth.Api/Controllers`), decorated by `AddHandlers` (logging, timing, exception mapping, validation). `Result<T>` errors map to `ProblemDetails` in `ApiController`.
- **The domain holds the rules** (`JTAuth.Domain`): `EmailAddress` (trim and lower-case, so one address is one identity), `LoginCodeRules` (6 digits, 10 minutes, 5 attempts, the keyed hash), `LoginCode` (`StateAt(now)` says usable, expired, used or out of attempts) and `TokenRules` (15-minute access tokens). Time always comes from `TimeProvider`.
- **Codes.** The stored value is HMAC-SHA256 over provider, address and code, keyed with `JTAuth:CodeSecret`; the table never holds a code. A new code ends the older open ones (`ConsumedUtc` is set), and verification only ever looks at the newest code of an address.
- **Attempts and single use are decided in SQL**, not in memory: `TryClaimAttemptAsync` increments `AttemptCount` only while the code is unused and below five, and is done *before* the digits are compared; `TryConsumeAsync` sets `ConsumedUtc` only if the code is unused and unexpired. Under any number of parallel callers exactly five guesses are counted and exactly one caller redeems a code (integration tests run both races).
- **Same answer for known and unknown addresses.** The request handler never reads accounts. Verification answers every code failure with the same `401 invalid_code`. Only the app (unknown `400`, disabled `403`), the shape of the address (`400`) and the rate limits (`429`) differ, and none of them depends on whether an account exists.
- **Rate limits.** Per address from the stored codes (1 a minute, 5 an hour, `JTAuth:CodeRequestsPerAddress...`), so they survive restarts and several instances. Per IP in the API (`RateLimits`): 120 requests a minute for any endpoint and 5 a minute / 20 an hour for code requests, chained in one global limiter. An app's web server calls JTAuth for its visitors and passes each visitor's address in `X-Forwarded-For`; JTAuth believes that header only from loopback, `RateLimits:TrustedProxies`, or any caller when `RateLimits:TrustAnyProxy` is set (a private network only), so the limits count visitors and not the web server. Hosts behind a platform proxy (Azure) must list or trust it, or set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`.
- **Accounts.** `UserRepository.GetOrCreateByIdentityAsync` finds the person who owns an `(Provider, ProviderSubject)` identity or creates the `[User]` and `UserIdentity` rows in one transaction; if a parallel sign-in wins the unique key, the loser rolls back and returns the winner, so there are no duplicate or orphan people. `isNewUser` is true only for the caller that created the account, and the display name stays empty (no `name` claim) until the app asks for one.
- **`UserClient`** is upserted at every successful sign-in with one `MERGE`: the first sign-in sets `FirstSignInUtc` and `LastSignInUtc`; later ones only move `LastSignInUtc` (never backwards); `ContactConsentUtc` and `RevokedUtc` are not mentioned by the statement. Open clients ignore the row when deciding who may sign in.
- **Timestamps** are sent to SQL as `datetime2` (`JTAuthDatabase` registers the Dapper type map), so a value reads back exactly as written.

## Tokens and published keys

- `AccessTokenIssuer` (Infrastructure) signs RS256 JWTs with `iss` (`JTAuth:Issuer`), `aud` (the requesting client's `Audience`), `sub` (user id), `name` (only when set), `iat`, `nbf`, `exp` (15 minutes) and a unique `jti`; the header carries `kid`.
- `RsaSigningKeys` loads (or on first run creates) a 2048-bit RSA key from a file outside the repository, `%LOCALAPPDATA%\JTAuth\signing-key.json` by default (`JTAuth:SigningKeyFile`; required outside Development). The `kid` is the SHA-256 of the public key. `PublishedKeys` is a list so rotation (publish the new key before signing with it) needs no change to the endpoints; Key Vault and rotation come with the Azure work.
- `GET /.well-known/jwks.json` publishes only `kty`, `use`, `alg`, `kid`, `n`, `e`; `GET /.well-known/openid-configuration` publishes the issuer and the JWKS URL. Both are anonymous with `Cache-Control: public, max-age=3600`.
- An app validates with stock `AddJwtBearer` (`Authority` = JTAuth's issuer, `Audience` = its own). Set `MapInboundClaims = false` to read `sub` and `name` under their own names. The end-to-end test does exactly this, one scheme per audience, and a token for `citybars` is refused by the other audience.
- **Email.** `IEmailSender` is in Application. Development registers `LoggingEmailSender`, which writes the message (code included) to the log. Any other environment refuses to start until a real sender is registered.

## Sessions: refresh tokens and logout

```
sign-in ─► access token (15 min) + refresh token A1 ─ new chain (FamilyId)
refresh(A1) ─► A1 revoked, A2 issued in the same chain, new access token
refresh(A1) again (reuse) ─► whole chain revoked: A2 is dead too, the person signs in with a code
logout(A2) ─► the chain is revoked
```

**In plain words.** A sign-in gives the app two tokens: a short-lived **access token** (a signed JWT the app checks itself) and a long-lived **refresh token** (a random string only JTAuth understands).

- **Every 15 minutes or so:** while someone is using the app, it needs a new access token about every 15 minutes. It gets one by presenting the refresh token, so the person is never asked for a code again.
- **A new refresh token every time:** each refresh returns a new access token *and* a new refresh token, and the old refresh token is revoked at that moment (single use). If someone copies a refresh token and uses it, the real user's next refresh presents one that was already replaced; JTAuth sees that and ends the chain.
- **Only while in use:** the cycle runs only while the app is in use. If nobody opens the app, nothing refreshes and the last refresh token simply waits.
- **If the 30 days run out:** each refresh token is good for 30 days from the moment it is issued. If it is not used within that time it expires and the person enters a code again. Every refresh gives the new token a fresh 30 days.
- **The access token is not stored:** JTAuth keeps no copy of it. Only the hash of the refresh token is in the database (`RefreshToken.TokenHash`).

**How it works.**

- A refresh token is 256 random bits, URL-safe, and only its SHA-256 is stored (`RefreshTokenRules`). It is bound to one person and one app; each use issues the next token in the chain, valid 30 days from that moment (sliding, no absolute cap).
- **Rotation is one SQL transaction** (`RefreshTokenRepository.RotateAsync`): a conditional `UPDATE ... WHERE RevokedUtc IS NULL AND ExpiresUtc > now` revokes the token, and only if that changed a row is the replacement inserted and `ReplacedByTokenId` set. Of any number of parallel requests with the same token exactly one wins; the losers are treated as reuse and end the chain (an integration test runs the race).
- **Reuse detection.** A token presented after it was revoked (already used, or logged out) ends its whole chain with one `UPDATE ... WHERE FamilyId = ...`; other chains of the same person, such as another device, are untouched. A token presented to a different app than it was issued to, an unknown token and an expired one are refused without revoking anything. Every failure is the same `401 invalid_refresh_token`. A person who refreshes from two places at once with the same token therefore gets signed out: callers should serialize refreshes (a backend-for-frontend does).
- A refresh reads the person afresh (`IUserRepository.FindByIdAsync`), so a changed display name is in the next access token's `name` claim. A refresh is not a sign-in, so it does not touch `UserClient`.
- **Logout** revokes the presented token's chain and answers `204` for any token, so it says nothing about which tokens exist.

## Sign-in time and deleting an account

- **`auth_time`.** A sign-in (a code or Google) starts a chain of refresh tokens, and `RefreshToken.SignedInUtc` (migration `0004`) records when. Every replacement in the chain carries the same time (`RefreshToken.Next`), and `AccessTokenIssuer` puts it in each access token as `auth_time` (seconds since 1970). Refreshing is not signing in again, so the time stays that of the sign-in; a token stored before the column existed falls back to its creation time. An app uses it to ask for a recent sign-in before something that cannot be undone.
- **`DELETE /api/v1/profile`** (`DeleteAccountHandler`) deletes the person's account for the app the token was issued for (the `aud`). It needs `auth_time` within `AccountRules.RecentSignInWindow` (10 minutes, one minute of clock difference allowed), otherwise `403` and nothing changes. It answers `{ accountRemoved }`.
- **One app, or all of them.** `UserRepository.DeleteAccountAsync`, in one transaction that locks the person: if the person also has a `UserClient` row or refresh tokens for another app, only this app's sessions and `UserClient` row are removed (`accountRemoved: false`) and the person can still sign in to the other app; otherwise the sign-in codes sent to their addresses, their identities, every session and the person are removed, so nothing that says who they are is kept (`accountRemoved: true`). The same address can sign up again later, as a new person with a new id.
- **After it.** The access token still validates until it expires (15 minutes) but the person is gone: `GET /profile` answers `401` ("This account no longer exists") and a repeated delete finds no one, which the app treats as "already done".
- Tests: `DeleteAccountHandlerTests`, `RefreshTokenTests`, `RefreshTokensHandlerTests` and `AccountDeletionEndToEndTests` (against SQL Server).

## The profile

- `GET`/`PUT /api/v1/profile` and the two email-change calls need an access token. JTAuth validates it itself with `AddJwtBearer`, using the public half of its own signing key (`RsaSigningKeys.ValidationKeys`), the configured issuer and the 15-minute expiry. The audience is not fixed (a token for any app is a token for the same person); instead an `OnTokenValidated` check requires the `aud` to belong to a registered, enabled app, so a disabled app's tokens stop working here as well.
- The profile lists how the person can sign in: Email (`Linked` with its address as `value`, or `NotLinked`), Google (`Linked` or `NotLinked`; the Google subject is never returned) and Phone (`ComingSoon`).
- **Display name** rules are in `DisplayName` (trimmed, 1 to 50 characters, one line, no control characters).
- **Changing the sign-in email** reuses the sign-in code machinery: `LoginCodeFlow` (Application) holds sending a code with its limits and redeeming it with its attempt and single-use rules, and both sign-in and the email change call it, so there is one set of rules. The code is sent to the new address, naming the app of the token's `aud`. If another account owns the address the answer is the same `202` but no code is sent. `UserRepository.ChangeEmailAsync` moves the person's email identity (or adds one for a person with none) in a transaction; the unique key on `(Provider, ProviderSubject)` settles a race between two people wanting the same address (one gets `409 email_in_use`).

## Google Sign-In

```
Page ─ Google Identity Services ─► ID token (a JWT signed by Google, audience = our Google client id)
Page ─► app web server ─ POST /api/v1/auth/google {clientId, idToken} ─► SignInWithGoogleHandler
     client ok? ► GoogleIdTokenValidator ► find, link or create the person ► UserClient ► the same tokens as a code sign-in
```

- **Validation** (`GoogleIdTokenValidator`, Infrastructure): `JsonWebTokenHandler` checks the RS256 signature against the keys in Google's discovery document (`https://accounts.google.com/.well-known/openid-configuration`, fetched over HTTPS and cached by a `ConfigurationManager`, which refetches when a key is not found), the issuer (`https://accounts.google.com` or `accounts.google.com`), the expiry, and that the audience is one of `JTAuth:GoogleClientIds`. Only RS256 is accepted. A token with no verified email (`email_verified`, as a boolean or as text), no `sub` or no email is refused. Every refusal is the same `401 invalid_google_token`. Blank client ids in configuration are ignored, so one cannot match a token that names no audience. No Google secret is involved: JTAuth only checks tokens, it never exchanges a code with Google, so the OAuth client secret is never needed or stored.
- **The account rule** (`UserRepository.GetOrCreateByGoogleAsync`, one transaction, retried when two sign-ins race on a unique key): the Google account already known signs in; otherwise the person who has this verified email as an Email identity gains the Google identity (`Linked`); otherwise a person is created with both a Google and an Email identity (`Created`), with Google's name as the display name when it has one. A person whose email account already has a *different* Google account linked is refused (`409`) instead of being given a second one. The uniqueness rules do the rest: an address and a Google account each belong to at most one person, however they are first used (integration tests race Google and code sign-ups of the same address).
- **Why linking by email is safe.** Both sides are proved: the existing Email identity by a code, the Google one by Google's `email_verified`. An unverified Google email never links, and the existing person's name is never overwritten by Google's.
- **Client ids are a list** (`JTAuth:GoogleClientIds`): one for each environment (the Test client for `localhost`, a Prod client for the real site) and one for each phone platform, which Google requires to be separate clients. Empty means the endpoint answers `404` and Google is never contacted. The ids are public values (they appear in the page); the client secret from Google's download file is not used and must not be kept in either repository.
- **Not done:** an ID token has no unique id to remember, so one stolen within its hour of validity could be replayed; the app sends it over TLS straight from Google's script to its own server and JTAuth issues its own short-lived tokens in exchange, which is the usual trade-off. A `nonce` can be added if it is ever needed. The Google profile picture is not stored.

## Service keys and the contact lookup

An app's server (not a browser, not a person) can ask JTAuth about the app's own people, identified by a **service key**.

- **The key.** A long random value the app keeps in its secrets (`JTAuth__ServiceKey` for CityBars). JTAuth stores only its SHA-256 in `Client.ServiceKeyHash` (`ServiceKeyRules.Hash`), so the database never holds a key that works. `scripts/set-service-key.ps1 -ClientId citybars` sets or replaces it (printing a new random one once when none is given) and `-Remove` clears it. A client with no key cannot ask.
- **The call.** `POST /api/v1/contacts/lookup` with `Authorization: Bearer <key>` and `{ userIds }` (`ContactsController`, `LookupContactsHandler`). The key is looked up first, so a caller without a valid one learns nothing about the request; a disabled client is 403. It returns the sign-in email only for people with a `UserClient` row for the **calling** client who are not revoked, so one app cannot read another app's people. Not cached; logged with the client and counts, never the addresses.
- **Not consent.** The lookup is for an app's staff to tell its accounts apart. Contacting people (a newsletter) is the consent-gated `GET /contacts` that is still to come.
- **Tests.** `LookupContactsHandlerTests` (the key, another app's key, a disabled app, the 200 limit, people of other apps) and `ContactsEndToEndTests` (real database over HTTP, including a person who signed in to two apps).

## Running and testing

- Local: `./scripts/deploy-db.ps1 -Environment local -Server 'localhost\SQLEXPRESS'`, set `ConnectionStrings__JTAuthDb` for the named instance, then `dotnet run --project JTAuth.Api` (https://localhost:7200). Development settings supply the issuer and a development-only `JTAuth:CodeSecret`.
- `./scripts/run-tests.ps1` runs the unit and architecture test projects directly (`dotnet test` reports "Zero tests ran" on this SDK); `-IncludeIntegration -Server 'localhost\SQLEXPRESS'` (or `JTAUTH_TEST_SQLSERVER`) adds the integration tests, which deploy the real migrations and seed into a throwaway database, or skip when no SQL Server (and no Docker) is available.
- Unit tests use in-memory repositories that follow the SQL rules, so whole sign-in flows run without a database; the integration tests prove the SQL itself, including both races, and start the API in memory for the end-to-end checks.

## Decisions (ADRs)

| # | Decision | Why |
|---|---|---|
| 1 | Own small identity service, not ASP.NET Core Identity | Identity is EF Core-based and password-centric, and needs OpenIddict on top to issue multi-app tokens. JTAuth issues standard JWTs (asymmetric key, JWKS endpoint) that any app validates with stock `AddJwtBearer`; it can move to OpenIddict or a hosted provider later without changing the token shape. |
| 2 | Separate repository and database, named `JTAuth` | One account across all of Jackie Trillo's apps; the umbrella prefix keeps app names out of shared infrastructure. |
| 3 | `JTAuth.BuildingBlocks` copied from CityBars | About 300 lines; a NuGet package is worth it only when a third repository needs it. |
| 4 | Registered clients in a `Client` table with per-client audience | Each app's tokens are rejected by every other app; sign-in emails name the right app. |
| 5 | Codes are stored as a keyed HMAC, and attempts are claimed in SQL before the comparison | A million possible codes fall to a plain hash if the table leaks; claiming first and atomically keeps parallel guessing to five attempts, and a conditional `UPDATE` makes a code single use without locks. |
| 6 | Per-address code limits are counted from the stored codes; per-IP limits live in the API | The address limit then holds across restarts and instances and needs no cache. Addresses of callers are not stored. |
| 7 | RS256 with a key file outside the repository | Every JWT library validates RS256 from a JWKS; the file is generated on first run and never committed, and Key Vault replaces it in Azure. |
| 8 | `UserClient` is written with one `MERGE` that mentions only the sign-in times | Consent and revocation belong to later features and must survive every sign-in; leaving them out of the statement guarantees it. |
| 9 | Unit tests use in-memory repositories that mirror the SQL rules; SQL is proved by integration tests | Whole flows (five wrong attempts then the right code, replaced codes, rate limits) run in milliseconds, while the race conditions are tested where they actually happen. |
| 10 | A refresh-token chain per sign-in; reuse ends that chain only | A leaked token should log out the device it leaked from, not every device the person uses. Rotation and reuse detection are decided by conditional `UPDATE`s, so they hold under parallel requests. No absolute chain lifetime: a regular user is never asked for a code again, and the cost of a stolen chain is bounded by detection on the owner's next refresh. |
| 11 | JTAuth validates its own access tokens for the profile, with a registered-and-enabled-app check instead of a fixed audience | The profile belongs to the person, whichever app the token came from; checking the audience against the client table still switches off a disabled app's tokens. |
| 12 | Changing the sign-in email goes through the same code rules as signing in, and a taken address gets the same answer but no code | One set of tested rules; a signed-in person cannot probe which addresses have accounts. |
| 13 | A refresh is not a sign-in | `UserClient.LastSignInUtc` keeps meaning "the last time the person proved who they are with a code". |
| 14 | A new Google account also gets an Email identity | The address Google verified is then owned by one person, so a later code sign-in with it reaches the same account, and the unique key on the address settles every race. |
| 15 | Google client ids are a configured list, and no Google secret is used | A Google token names the client it was issued for, so Test, Prod and each phone platform are different clients; JTAuth only validates tokens, so the secret in Google's download file has no use here and is better not to have in a repository. |
