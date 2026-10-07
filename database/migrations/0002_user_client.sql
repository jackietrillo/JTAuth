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
