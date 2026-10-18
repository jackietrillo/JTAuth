-- JTAuth database: when the person last proved who they are, so an app can ask for a recent sign-in before something that cannot be undone
-- (deleting an account). A chain of refresh tokens starts at a sign-in (a code or Google); every token replaced in it carries the same
-- time forward, and the access tokens issued from it say so in their auth_time claim. Rows from before this migration have none: for
-- them the time the token was created stands in.

ALTER TABLE dbo.RefreshToken ADD SignedInUtc datetime2(3) NULL;
GO
