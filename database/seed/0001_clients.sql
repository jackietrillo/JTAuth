-- Client apps that sign users in through JTAuth. Idempotent: runs on every deploy.
-- Registering another app means adding a row here (with a new, never-reused Id).
-- IsEnabled is left alone once the row exists, so a client can be switched off without a redeploy undoing it.

SET IDENTITY_INSERT dbo.Client ON;

MERGE dbo.Client AS target
USING (VALUES
    (1, 'citybars', N'CityBars', 'citybars')
) AS source (Id, ClientId, Name, Audience)
ON target.Id = source.Id
WHEN MATCHED AND (target.ClientId <> source.ClientId OR target.Name <> source.Name OR target.Audience <> source.Audience) THEN
    UPDATE SET ClientId = source.ClientId, Name = source.Name, Audience = source.Audience
WHEN NOT MATCHED BY TARGET THEN
    INSERT (Id, ClientId, Name, Audience, IsEnabled) VALUES (source.Id, source.ClientId, source.Name, source.Audience, 1);

SET IDENTITY_INSERT dbo.Client OFF;
