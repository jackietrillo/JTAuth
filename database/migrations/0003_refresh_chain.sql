-- JTAuth database: what rotating refresh tokens need.
-- Each sign-in with an emailed code starts a chain of refresh tokens; every use replaces the token with the next one in the same
-- chain. Presenting a token that was already used (or logged out) ends the whole chain, and only that chain: the same person
-- signed in on another device has a chain of its own.

-- The chain a token belongs to. Rows that exist before this migration (none are issued yet) each become a chain of their own.
ALTER TABLE dbo.RefreshToken ADD FamilyId uniqueidentifier NOT NULL CONSTRAINT DF_RefreshToken_FamilyId DEFAULT (NEWID());
GO

-- The token that replaced this one when it was used, so a chain can be followed from its first token to its last.
ALTER TABLE dbo.RefreshToken ADD ReplacedByTokenId bigint NULL;
GO

ALTER TABLE dbo.RefreshToken ADD CONSTRAINT FK_RefreshToken_ReplacedBy FOREIGN KEY (ReplacedByTokenId) REFERENCES dbo.RefreshToken (Id);
GO

-- Ending a chain updates every token with the same FamilyId.
CREATE INDEX IX_RefreshToken_FamilyId ON dbo.RefreshToken (FamilyId);
GO
