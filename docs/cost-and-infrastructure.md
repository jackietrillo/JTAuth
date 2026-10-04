# JTAuth: Cost and Infrastructure

**Binding for the build.** The spec ([jtauth-agent-prompt.md](../jtauth-agent-prompt.md), Section 2c) points here. The same principles apply to every app of Jackie Trillo's; the CityBars doc (`C:\JTRepos\CityBars\docs\cost-and-infrastructure.md`) holds the full external-API rules. The monthly estimate and the Azure layout are filled in during Step 6.

## 1. Principle

**Cost is a requirement.** Azure is the expensive part of these apps; the code is free. Pick the cheapest option that does the job, and add a paid service only with its **free allowance**, its **trigger for paying** and a **cap**. Prices and free tiers change: **re-read current pricing before building each item** and record the figures in section 5. Do not quote prices from memory.

## 2. Develop for free

Everything runs locally; nothing needs Azure until **Step 6**. Development sign-in codes are written to the log, so no email service is needed locally.

## 3. Azure

- **Lowest tier that works, scale to zero where possible** for the API (App Service free or smallest shared tier, or Container Apps on the consumption plan).
- **Database:** Azure SQL serverless with **auto-pause** and the smallest size (or the free offer if it applies); no geo-replication or zone redundancy. The `JTAuth` database shares a logical SQL server with the apps' databases where that saves money.
- **Telemetry:** Application Insights with **sampling and a daily data cap**.
- **Budget alert** at 50%, 80% and 100% of a monthly amount, resource tags, and a one-command tear-down for test environments.
- **No premium features** (Front Door, API Management, private endpoints, premium Key Vault, reserved capacity) without a written need and its cost.

## 4. Running cheaply

- **Email** (Azure Communication Services) is pay per message: only sign-in codes, already rate-limited per address and per IP, plus a **daily send cap**.
- **Google Sign-In** is free. No other paid API is used.
- Cache `/.well-known/jwks.json` and the openid-configuration with a long `Cache-Control`; apps cache the keys too, so JTAuth is not hit per request. Access tokens are validated by the apps locally, never by calling JTAuth.
- Rate-limit every anonymous endpoint per IP.

## 5. Monthly cost estimate

*To be filled in during Step 6, from the current pricing pages (record the date).*

| Item | Tier chosen | Estimated monthly cost | Pricing checked |
|---|---|---|---|
| API (App Service or Container Apps) | | | |
| Azure SQL (`JTAuth`) | | | |
| Application Insights (with daily cap) | | | |
| Key Vault | | | |
| Email (Azure Communication Services) | | | |
| **Total** | | | |

## 6. Azure layout

*To be filled in during Step 6: resource names (`jta-<env>-<resource>`), what shares a plan or server, the budget amount, and the tear-down command.*

## 7. Cost log

| Date | Decision | Effect |
|---|---|---|
| 2026-10-03 | Tokens validated locally by apps from cached JWKS keys | JTAuth is not called per request |
| 2026-10-03 | Sign-in codes only through email, with a daily send cap | Email spend bounded |
