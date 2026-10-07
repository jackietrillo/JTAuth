using System.Text.RegularExpressions;
using JTAuth.Database;
using Shouldly;

namespace JTAuth.UnitTests.Database;

public sealed partial class DatabaseScriptTests
{
    private static readonly string DatabaseFolder = Path.Combine(RepoRoot.Path, "database");

    private static FileInfo[] ScriptFiles(string kind) =>
        new DirectoryInfo(Path.Combine(DatabaseFolder, kind)).GetFiles("*.sql").OrderBy(file => file.Name, StringComparer.Ordinal).ToArray();

    [Theory]
    [InlineData("migrations")]
    [InlineData("seed")]
    public void ScriptsAreNumberedFromOneWithoutGaps(string kind)
    {
        var numbers = ScriptFiles(kind)
            .Select(file => NumberedScriptName().Match(file.Name))
            .Select(match => match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : -1)
            .ToList();

        numbers.ShouldNotBeEmpty();
        numbers.ShouldBe(Enumerable.Range(1, numbers.Count), $"{kind} scripts must be named NNNN_description.sql, numbered 0001, 0002, ...");
    }

    [Theory]
    [InlineData("migrations", DatabaseDeployer.MigrationsPrefix)]
    [InlineData("seed", DatabaseDeployer.SeedPrefix)]
    public void EveryScriptIsEmbeddedInTheMigrator(string kind, string prefix)
    {
        typeof(DatabaseDeployer).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ShouldBe(ScriptFiles(kind).Select(file => prefix + file.Name));
    }

    [Fact]
    public void SchemaReferenceContainsEveryTableFromTheMigrations()
    {
        var reference = File.ReadAllText(Path.Combine(DatabaseFolder, "schema.sql"));
        var tables = ScriptFiles("migrations")
            .SelectMany(file => CreateTable().Matches(File.ReadAllText(file.FullName)))
            .Select(match => match.Value)
            .ToList();

        tables.ShouldNotBeEmpty();
        tables.Where(statement => !reference.Contains(statement, StringComparison.Ordinal))
            .ShouldBeEmpty("database/schema.sql is out of date with database/migrations.");
    }

    [Fact]
    public void EverythingLivesInDboAndThereAreNoPasswords()
    {
        foreach (var file in ScriptFiles("migrations").Concat(ScriptFiles("seed")))
        {
            var sql = File.ReadAllText(file.FullName);
            sql.ShouldNotContain("CREATE SCHEMA", Case.Insensitive, $"{file.Name}: use dbo.");
            Regex.IsMatch(sql, @"\bPassword\w*\s+(n?varchar|varbinary|char|binary)", RegexOptions.IgnoreCase)
                .ShouldBeFalse($"{file.Name}: JTAuth is passwordless; there is no password column anywhere.");
        }
    }

    [Fact]
    public void SchemaReferenceHasASectionForEveryMigration()
    {
        var reference = File.ReadAllText(Path.Combine(DatabaseFolder, "schema.sql"));

        ScriptFiles("migrations")
            .Where(file => !reference.Contains($"-- {file.Name}", StringComparison.Ordinal))
            .Select(file => file.Name)
            .ShouldBeEmpty("database/schema.sql has no section for these migrations.");
    }

    [Fact]
    public void WhoHasUsedWhichAppIsRecordedOncePerPersonAndApp()
    {
        var migration = File.ReadAllText(ScriptFiles("migrations").Single(file => file.Name.EndsWith("_user_client.sql", StringComparison.Ordinal)).FullName);
        var reference = File.ReadAllText(Path.Combine(DatabaseFolder, "schema.sql"));

        foreach (var sql in new[] { migration, reference })
        {
            sql.ShouldContain("CREATE TABLE dbo.UserClient");
            sql.ShouldContain("CONSTRAINT PK_UserClient PRIMARY KEY CLUSTERED (UserId, ClientId)");
            sql.ShouldContain("FK_UserClient_User FOREIGN KEY (UserId) REFERENCES dbo.[User] (Id)");
            sql.ShouldContain("FK_UserClient_Client FOREIGN KEY (ClientId) REFERENCES dbo.Client (Id)");
        }
    }

    [Fact]
    public void ConsentToBeContactedIsKeptPerAppAndCanBeWithdrawn()
    {
        var migration = File.ReadAllText(ScriptFiles("migrations").Single(file => file.Name.EndsWith("_user_client.sql", StringComparison.Ordinal)).FullName);

        // It lives on the person-and-app row (so agreeing to one app says nothing about another) and is nullable (null = has not agreed).
        migration.ShouldContain("ContactConsentUtc datetime2(3)     NULL");
        migration.ShouldContain("WHERE ContactConsentUtc IS NOT NULL AND RevokedUtc IS NULL");
    }

    [Fact]
    public void AnAppsServiceKeyIsStoredOnlyAsAHash()
    {
        var migration = File.ReadAllText(ScriptFiles("migrations").Single(file => file.Name.EndsWith("_user_client.sql", StringComparison.Ordinal)).FullName);

        migration.ShouldContain("ADD ServiceKeyHash binary(32) NULL");
        Regex.IsMatch(migration, @"\b(ServiceKey|ServiceSecret|ApiKey|Secret)\s+(n?varchar|varbinary|binary|char)", RegexOptions.IgnoreCase)
            .ShouldBeFalse("only the hash of a service key may be stored");
    }

    [Fact]
    public void TheUserTableHoldsNoContactDetailsOfItsOwn()
    {
        var identity = File.ReadAllText(ScriptFiles("migrations").First().FullName);
        var user = Regex.Match(identity, @"CREATE TABLE dbo\.\[User\](?<body>.*?)\);", RegexOptions.Singleline).Groups["body"].Value;

        user.ShouldNotBeEmpty();
        user.ShouldNotContain("Email", Case.Insensitive);
        user.ShouldNotContain("Phone", Case.Insensitive);
    }

    [GeneratedRegex(@"^(\d{4})_[a-z0-9_]+\.sql$")]
    private static partial Regex NumberedScriptName();

    [GeneratedRegex(@"CREATE TABLE dbo\.\[?\w+\]?")]
    private static partial Regex CreateTable();
}
