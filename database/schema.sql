-- JTAuth database: full reference schema. Identity only, shared by every app that signs users in through JTAuth.
--
-- READ-ONLY REFERENCE. The database is built from the numbered migrations in database/migrations,
-- applied by scripts/deploy-db.ps1. When you add a migration, update this file to match;
-- a unit test (DatabaseScriptTests) fails if a table is missing here.
--
-- Everything is in dbo. Lookup data (the registered client apps) lives in database/seed.

-- ============================================================================
-- 0001_identity.sql
-- ============================================================================

-- JTAuth database: identity only, shared by every app that signs users in through JTAuth.
-- Nothing here knows about bars, cities or any app's preferences. There is no password column anywhere.
-- [User] is a reserved word: always write it bracketed.

-- Each app that uses JTAuth. Tokens carry the client's Audience as 'aud'.
CREATE TABLE dbo.Client
(
    Id         int IDENTITY(1, 1) NOT NULL,
    ClientId   varchar(50)        NOT NULL, -- sent by the app, e.g. 'citybars'
    Name       nvarchar(100)      NOT NULL, -- shown in sign-in emails: 'Your CityBars code is ...'
    Audience   varchar(100)       NOT NULL,
    IsEnabled  bit                NOT NULL CONSTRAINT DF_Client_IsEnabled DEFAULT (1),
    CreatedUtc datetime2(3)       NOT NULL CONSTRAINT DF_Client_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_Client PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT UQ_Client_ClientId UNIQUE (ClientId)
);
GO

CREATE TABLE dbo.[User]
(
    Id          uniqueidentifier NOT NULL CONSTRAINT DF_User_Id DEFAULT (NEWSEQUENTIALID()), -- JWT 'sub'
    DisplayName nvarchar(50)     NULL, -- set in the new-user step after the first sign-in; JWT 'name'
    CreatedUtc  datetime2(3)     NOT NULL CONSTRAINT DF_User_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_User PRIMARY KEY CLUSTERED (Id)
);
GO

-- How a user signs in. Each email and each phone number belongs to at most one account
-- (unique individually), and a user has at most one identity per provider.
CREATE TABLE dbo.UserIdentity
(
    Id              bigint IDENTITY(1, 1) NOT NULL,
    UserId          uniqueidentifier      NOT NULL,
    Provider        varchar(16)           NOT NULL,
    ProviderSubject nvarchar(320)         NOT NULL, -- Google subject id, normalized email, or E.164 phone
    VerifiedUtc     datetime2(3)          NOT NULL,
    CONSTRAINT PK_UserIdentity PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_UserIdentity_User FOREIGN KEY (UserId) REFERENCES dbo.[User] (Id),
    CONSTRAINT UQ_UserIdentity_Provider_ProviderSubject UNIQUE (Provider, ProviderSubject),
    CONSTRAINT UQ_UserIdentity_UserId_Provider UNIQUE (UserId, Provider),
    CONSTRAINT CK_UserIdentity_Provider CHECK (Provider IN ('Google', 'Email', 'Phone'))
);
GO

-- One-time sign-in codes, stored only as a hash.
CREATE TABLE dbo.LoginCode
(
    Id              bigint IDENTITY(1, 1) NOT NULL,
    Provider        varchar(16)           NOT NULL,
    ProviderSubject nvarchar(320)         NOT NULL,
    CodeHash        varbinary(64)         NOT NULL,
    ExpiresUtc      datetime2(3)          NOT NULL,
    AttemptCount    int                   NOT NULL CONSTRAINT DF_LoginCode_AttemptCount DEFAULT (0),
    ConsumedUtc     datetime2(3)          NULL,
    CreatedUtc      datetime2(3)          NOT NULL CONSTRAINT DF_LoginCode_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_LoginCode PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT CK_LoginCode_Provider CHECK (Provider IN ('Email', 'Phone')),
    CONSTRAINT CK_LoginCode_AttemptCount CHECK (AttemptCount >= 0)
);
GO

CREATE INDEX IX_LoginCode_Provider_ProviderSubject ON dbo.LoginCode (Provider, ProviderSubject) INCLUDE (CreatedUtc);
GO

-- Refresh tokens, stored only as a hash, each issued to one client app.
CREATE TABLE dbo.RefreshToken
(
    Id         bigint IDENTITY(1, 1) NOT NULL,
    UserId     uniqueidentifier      NOT NULL,
    ClientId   int                   NOT NULL,
    TokenHash  varbinary(64)         NOT NULL,
    ExpiresUtc datetime2(3)          NOT NULL,
    RevokedUtc datetime2(3)          NULL,
    CreatedUtc datetime2(3)          NOT NULL CONSTRAINT DF_RefreshToken_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_RefreshToken PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_RefreshToken_User FOREIGN KEY (UserId) REFERENCES dbo.[User] (Id),
    CONSTRAINT FK_RefreshToken_Client FOREIGN KEY (ClientId) REFERENCES dbo.Client (Id),
    CONSTRAINT UQ_RefreshToken_TokenHash UNIQUE (TokenHash)
);
GO

CREATE INDEX IX_RefreshToken_UserId ON dbo.RefreshToken (UserId);
GO

-- ============================================================================
-- 0002_user_client.sql
-- ============================================================================

-- JTAuth database: which user has used which app, and the key an app's servers use to ask for its own users' contact details.
-- Open clients accept any user whether or not a row exists here; the row is a record, not a gate. It is written at every
-- successful sign-in, so a later restriction on an app only has to start checking it.

-- The SHA-256 of the key an app's servers present to read that app's contacts. The key itself is never stored.
ALTER TABLE dbo.Client ADD ServiceKeyHash binary(32) NULL;
GO

-- One row per person and app, created at the person's first successful sign-in to that app.
-- ContactConsentUtc is when the person agreed that this app may contact them (cleared to withdraw); consent is per app.
-- RevokedUtc is used only when a client is restricted to chosen users.
CREATE TABLE dbo.UserClient
(
    UserId            uniqueidentifier NOT NULL,
    ClientId          int              NOT NULL,
    FirstSignInUtc    datetime2(3)     NOT NULL CONSTRAINT DF_UserClient_FirstSignInUtc DEFAULT (SYSUTCDATETIME()),
    LastSignInUtc     datetime2(3)     NOT NULL CONSTRAINT DF_UserClient_LastSignInUtc DEFAULT (SYSUTCDATETIME()),
    ContactConsentUtc datetime2(3)     NULL,
    RevokedUtc        datetime2(3)     NULL,
    CONSTRAINT PK_UserClient PRIMARY KEY CLUSTERED (UserId, ClientId),
    CONSTRAINT FK_UserClient_User FOREIGN KEY (UserId) REFERENCES dbo.[User] (Id),
    CONSTRAINT FK_UserClient_Client FOREIGN KEY (ClientId) REFERENCES dbo.Client (Id),
    CONSTRAINT CK_UserClient_LastSignIn CHECK (LastSignInUtc >= FirstSignInUtc)
);
GO

-- The contacts list of one app reads exactly the people who agreed and are not revoked, in order of user id.
-- A filtered index needs QUOTED_IDENTIFIER ON to write to the table: SqlClient does, a hand-run sqlcmd needs -I.
CREATE INDEX IX_UserClient_ClientId_UserId_Contactable ON dbo.UserClient (ClientId, UserId)
    WHERE ContactConsentUtc IS NOT NULL AND RevokedUtc IS NULL;
GO
